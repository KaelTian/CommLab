using CommLab.Core.Heartbeat;
using CommLab.Core.Policies;
using CommLab.Core.Protocol;
using CommLab.Core.Transport;
using Serilog;
using System.Net.Sockets;

namespace CommLab.Core
{
    public enum ConnState { Disconnected, Connecting, Connected, Suspected }
    public class DeviceClient
    {
        private static readonly ILogger Logger = Log.ForContext<DeviceClient>();

        private readonly TcpTransport _transport = new();
        private readonly RetryPolicy _retryPolicy = new(maxRetires: 3, baseDelay: TimeSpan.FromMilliseconds(200));
        private readonly HeartbeatService _heartbeat;
        private readonly SemaphoreSlim _sendLock = new(1, 1); // 业务命令串行
        private readonly SemaphoreSlim _connectLock = new(1, 1); // 同一时刻只允许一个重连循环
        private readonly string _ip;
        private readonly int _port;
        private CancellationTokenSource? _appCts;
        private CancellationTokenSource? _heartbeatCts;
        private TaskCompletionSource<DeviceFrame>? _pendingResponse;
        private byte _expectedAckCmd;
        private volatile bool _stopping;

        public ConnState State { get; private set; } = ConnState.Disconnected;
        public event Action<ConnState>? OnStateChanged;

        public DeviceClient(string ip, int port)
        {
            _ip = ip;
            _port = port;
            _heartbeat = new HeartbeatService(
                interval: TimeSpan.FromSeconds(2),
                ackTimeout: TimeSpan.FromSeconds(1),
                missThreshold: 3,
                beat: SendHeartbeatAsync
                );

            _heartbeat.OnSuspected += () => SetState(ConnState.Suspected);
            _heartbeat.OnConnectionRecovered += () => SetState(ConnState.Connected);
            _heartbeat.OnConnectionLost += reason => HandleConnectionLost($"心跳丢失: {reason}");

            _transport.OnFrameReceived += Transport_OnFrameReceived;
            _transport.OnReceiveFailed += ex => HandleConnectionLost($"接收循环异常: {ex.Message}");
        }

        public Task StartAsync(CancellationToken ct)
        {
            _appCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = Task.Run(() => ConnectLoopAsync(_appCts.Token)); // 后台连接，不阻塞启动

            return Task.CompletedTask;
        }

        /// <summary>
        /// 业务唯一入口：有限重试 + 单次超时 + 掉线等待重连
        /// </summary>
        /// <param name="cmd"></param>
        /// <param name="payload"></param>
        /// <param name="timeout"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        /// <exception cref="TimeoutException"></exception>
        public async Task<DeviceFrame> SendCommandAsync(byte cmd, byte[] payload, TimeSpan timeout, CancellationToken ct)
        {
            var request = new DeviceFrame { Cmd = cmd, Payload = payload };
            _expectedAckCmd = GetAckCmd(cmd);

            return await _retryPolicy.ExecuteAsync(
                action: async (_, innerCt) =>
                {
                    if (State != ConnState.Connected)
                    {
                        Logger.Information("连接未就绪，等待重连完成...");
                        await WaitUntilConnectedAsync(innerCt);
                    }

                    await _sendLock.WaitAsync(innerCt);
                    try
                    {
                        _pendingResponse = new TaskCompletionSource<DeviceFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
                        await _transport.SendAsync(request.Serialize(), innerCt);

                        var delayTask = Task.Delay(timeout, innerCt);
                        var completed = await Task.WhenAny(_pendingResponse.Task, delayTask);
                        if (completed != _pendingResponse.Task)
                        {
                            innerCt.ThrowIfCancellationRequested();
                            throw new TimeoutException($"等待设备应答超时 ({timeout.TotalSeconds}s)");
                        }
                        return await _pendingResponse.Task;
                    }
                    finally
                    {
                        _pendingResponse = null;
                        _sendLock.Release();
                    }
                },
                isTransient: ex => ex is TimeoutException or IOException or SocketException,
                ct: ct);
        }
        /// <summary>
        /// 应答分发：区分心跳应答 / 业务应答
        /// </summary>
        /// <param name="frame"></param>
        private void Transport_OnFrameReceived(DeviceFrame frame)
        {
            if (frame.Cmd == DeviceFrame.CmdHeartbeatAck)
            {
                _heartbeat.NotifyAckReceived();
                return;
            }
            if (frame.Cmd == DeviceFrame.CmdError)
            {
                Logger.Error("设备返回错误帧");
                _pendingResponse?.TrySetException(new InvalidOperationException("设备返回错误"));
                return;
            }
            if (_pendingResponse != null && frame.Cmd == _expectedAckCmd)
                _pendingResponse.TrySetResult(frame);
            else
                Logger.Debug("收到未匹配的帧 Cmd=0x{Cmd:X2}", frame.Cmd);

        }
        /// <summary>
        /// 连接管理
        /// </summary>
        /// <param name="ct"></param>
        /// <returns></returns>
        private async Task ConnectLoopAsync(CancellationToken ct)
        {
            await _connectLock.WaitAsync(ct);
            try
            {
                var backoff = TimeSpan.FromSeconds(1);
                while (!ct.IsCancellationRequested && !_stopping)
                {
                    SetState(ConnState.Connecting);
                    try
                    {
                        Logger.Information("正在连接 {Ip}:{Port} ...", _ip, _port);
                        await _transport.ConnectAsync(_ip, _port, TimeSpan.FromSeconds(3), ct);
                        Logger.Information("★ 连接成功");
                        SetState(ConnState.Connected);
                        backoff = TimeSpan.FromSeconds(1);

                        _heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        _ = Task.Run(() => _heartbeat.RunAsync(_heartbeatCts.Token));
                        return; // 接收循环交给 Transport；掉线由心跳/接收异常触发下一轮
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Logger.Warning("连接失败: {Msg}，{Sec}s 后重试", ex.Message, backoff.TotalSeconds);
                        try
                        {
                            await Task.Delay(backoff, ct);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                        backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
                    }
                }
            }
            finally { _connectLock.Release(); }
        }

        private void HandleConnectionLost(string reason)
        {
            if (State == ConnState.Disconnected) return; // 重连已在进行，去重
            Logger.Warning("⚠ 连接丢失：{Reason}", reason);
            SetState(ConnState.Disconnected);
            _heartbeatCts?.Cancel();
            _pendingResponse?.TrySetException(new IOException("连接已断开: " + reason)); // 进行中的命令快速失败
            _ = Task.Run(() => ConnectLoopAsync(_appCts!.Token));
        }

        private async Task SendHeartbeatAsync(CancellationToken ct)
        {
            if (State != ConnState.Connected)
                throw new IOException("连接未就绪，跳过本轮心跳");
            await _transport.SendAsync(new DeviceFrame
            {
                Cmd = DeviceFrame.CmdHeartbeat
            }.Serialize(), ct);
        }

        private async Task WaitUntilConnectedAsync(CancellationToken ct)
        {
            while (State != ConnState.Connected && !ct.IsCancellationRequested)
                await Task.Delay(200, ct);
            ct.ThrowIfCancellationRequested();
        }

        private static byte GetAckCmd(byte cmd) => cmd switch
        {
            DeviceFrame.CmdHeartbeat => DeviceFrame.CmdHeartbeatAck,
            DeviceFrame.CmdReadRegister => DeviceFrame.CmdReadRegisterAck,
            _ => (byte)(cmd | 0x80)
        };

        private void SetState(ConnState s)
        {
            if (State == s) return;
            State = s;
            Logger.Information("═══ 状态变化: {Old} → {New} ═══", Enum.GetName(State), Enum.GetName(s));
            OnStateChanged?.Invoke(s);
        }

        public async Task StopAsync()
        {
            _stopping = true;
            _appCts?.Cancel();
            _heartbeatCts?.Cancel();
            await Task.Delay(300);
            _transport.Disconnect();
            SetState(ConnState.Disconnected);
        }

        private static string EnumGetName(ConnState s) => s.ToString();
    }
}
