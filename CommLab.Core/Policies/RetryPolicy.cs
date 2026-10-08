using Serilog;

namespace CommLab.Core.Policies
{
    public class RetryPolicy
    {
        private static readonly ILogger Logger = Log.ForContext<RetryPolicy>();
        private readonly int _maxRetries;
        private readonly TimeSpan _baseDelay;
        private static readonly Random Rng = new();

        public RetryPolicy(int maxRetires = 3, TimeSpan? baseDelay = null)
        {
            _maxRetries = maxRetires;
            _baseDelay = baseDelay ?? TimeSpan.FromMilliseconds(200);
        }

        public async Task<T> ExecuteAsync<T>(
            Func<int, CancellationToken, Task<T>> action,
            Func<Exception, bool> isTransient,
            CancellationToken ct
            )
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (attempt > 0)
                        Logger.Debug("第 {Attempt} 次重试执行中...", attempt + 1);
                    return await action(attempt, ct);
                }
                catch (Exception ex) when (attempt < _maxRetries && isTransient(ex) &&
                !ct.IsCancellationRequested)
                {
                    var delay = TimeSpan.FromMilliseconds(
                        _baseDelay.TotalMilliseconds * Math.Pow(2, attempt)); // 200 -> 400 -> 800ms
                    var jitter = delay.TotalMilliseconds * 0.25 * (Rng.NextDouble() * 2 - 1);
                    delay += TimeSpan.FromMilliseconds(jitter); // ±25% 抖动
                    Logger.Warning("第 {Attempt}/{Max} 次尝试失败: {Msg}，{Ms:F0}ms 后重试",
                        attempt + 1, _maxRetries + 1, ex.Message, delay.TotalMilliseconds);
                    await Task.Delay(delay, ct);
                }
            }
        }
    }
}
