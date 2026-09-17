namespace LiveCaptionsTranslator.utils
{
    public class RateLimiter
    {
        private DateTime _lastCall = DateTime.MinValue;
        private readonly int _minIntervalMs;
        private readonly SemaphoreSlim _semaphore = new(1, 1);

        public RateLimiter(int minIntervalMs = 200)
        {
            _minIntervalMs = minIntervalMs;
        }

        public async Task WaitForNextCall(CancellationToken token = default)
        {
            await _semaphore.WaitAsync(token);
            try
            {
                var elapsed = (DateTime.Now - _lastCall).TotalMilliseconds;
                if (elapsed < _minIntervalMs)
                {
                    await Task.Delay(_minIntervalMs - (int)elapsed, token);
                }
                _lastCall = DateTime.Now;
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }
}
