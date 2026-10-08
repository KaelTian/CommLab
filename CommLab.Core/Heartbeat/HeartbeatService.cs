using Serilog;

namespace CommLab.Core.Heartbeat
{
    public class HeartbeatService
    {
        private static readonly ILogger Logger = Log.ForContext<HeartbeatService>();
        private readonly TimeSpan _interval, _ackTimeout;
        private readonly int _missThreshold;
        private readonly Func<CancellationToken, Task> _beat;
        private TaskCompletionSource? _ackTcs;

        public event Action<string>? OnConnectionLost; // 连续丢失 → 触发重连
        public event Action? OnSuspected; // 第一次丢失 → 状态降级
        public event Action? OnConnectionRecovered; // 丢失后恢复

        public HeartbeatService(TimeSpan interval, TimeSpan ackTimeout, int missThreshold, Func<CancellationToken, Task> beat)
        {
            _interval = interval;
            _ackTimeout = ackTimeout;
            _missThreshold = missThreshold;
            _beat = beat;
        }

        /// <summary>由 DeviceClient 在收到心跳应答时调用</summary>
        public void NotifyAckReceived() => _ackTcs?.TrySetResult();

        public async Task RunAsync(CancellationToken ct)
        {
            int missed = 0;
            Logger.Information("心跳服务启动：间隔 {I}s，应答超时 {T}s，掉线阈值 {M} 次",
                _interval.TotalSeconds, _ackTimeout.TotalSeconds, _missThreshold);

            while (!ct.IsCancellationRequested)
            {
                _ackTcs = new
                    TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    await _beat(ct);
                    var timeoutTask = Task.Delay(_ackTimeout, ct);
                    if (await Task.WhenAny(_ackTcs.Task, timeoutTask) == _ackTcs.Task && !ct.IsCancellationRequested)
                    {
                        if (missed > 0)
                        {
                            Logger.Information("心跳恢复，连续丢失技术清零");
                            OnConnectionRecovered?.Invoke();
                        }
                        missed = 0;
                        Logger.Verbose("心跳 OK");
                    }
                    else
                    {
                        if (ct.IsCancellationRequested) break;
                        missed++;
                        Logger.Warning("心跳应答超时 ({Missed}/{Threshold})", missed, _missThreshold);
                        if (missed == 1) OnSuspected?.Invoke();
                        if (missed >= _missThreshold)
                        {
                            OnConnectionLost?.Invoke($"连续 {missed} 次心跳无应答");
                            return;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    missed++;
                    Logger.Warning(ex, "心跳发送异常 ({Missed}/{Threshold})", missed, _missThreshold);
                    if (missed == 1) OnSuspected?.Invoke();
                    if (missed >= _missThreshold)
                    {
                        OnConnectionLost?.Invoke(ex.Message);
                        return;
                    }
                }

                try { await Task.Delay(_interval, ct); }
                catch (OperationCanceledException) { break; }
            }
            Logger.Debug("心跳服务退出");
        }

    }
}
