using CommLab.Core.Protocol;
using Serilog;
using System.Net.Sockets;

namespace CommLab.Core.Transport
{
    public class TcpTransport
    {
        private static readonly ILogger Logger = Log.ForContext<TcpTransport>();

        public event Action<DeviceFrame>? OnFrameReceived; // 解析出一帧
        public event Action<Exception>? OnReceiveFailed; // 对端断开 / 读异常

        private TcpClient? _client;
        private NetworkStream? _stream;
        private CancellationTokenSource? _recvCts;

        private readonly List<byte> _buffer = new(); // 必须在 lock 内访问

        public bool IsConnected => _client?.Connected == true && _stream != null;

        public async Task ConnectAsync(string ip, int port, TimeSpan timeout, CancellationToken ct)
        {
            Disconnect();
            _client = new TcpClient { NoDelay = true };
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await _client.ConnectAsync(ip, port, cts.Token);
            _stream = _client.GetStream();
            Logger.Information("TCP 已连接 {Ip}:{Port}", ip, port);

            _recvCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = Task.Run(() => ReceiveLoopAsync(_recvCts.Token));
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var tmp = new byte[4096];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int n = await _stream!.ReadAsync(tmp, ct); // ★ 唯一读的地方
                    if (n == 0) // ★ 返回 0 = 对端关闭
                    {
                        Logger.Warning("对端关闭了连接");
                        OnReceiveFailed?.Invoke(new IOException("对端关闭了连接"));
                        return;
                    }
                    lock (_buffer)
                    {
                        _buffer.AddRange(new ReadOnlySpan<byte>(tmp, 0, n));
                        while (DeviceFrame.TryParse(_buffer, out var f))
                            OnFrameReceived?.Invoke(f!);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Warning(ex, "接收循环异常");
                OnReceiveFailed?.Invoke(ex);
            }
        }

        public async Task SendAsync(byte[] data, CancellationToken ct)
        {
            if (_stream == null) throw new IOException("未连接，无法发送");
            await _stream.WriteAsync(data, ct);
            Logger.Verbose("已发送 {Bytes} 字节", data.Length);
        }

        public void Disconnect()
        {
            try { _recvCts?.Cancel(); _stream?.Close(); _client?.Close(); }
            catch { /* 关闭时忽略异常 */}
            _stream = null; _client = null;
            lock (_buffer) _buffer.Clear();
        }
    }
}
