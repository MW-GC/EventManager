using System.Security.Cryptography;
using System.Text;

namespace MW_GC.EventManager.API.Services;

/// <summary>
/// A best-effort rate limit for the anonymous like route (#101): a fixed one-minute window per
/// visitor address, <see cref="Limit"/> requests per window. Every like request counts, whether the
/// token is valid or not, so the 429 says nothing about a token.
/// The state is in memory, so the limit is per Function instance: a scaled-out app allows
/// <see cref="Limit"/> per instance, and a restart forgets it. Addresses are kept only as an HMAC
/// under a random key that lives as long as the process; the address itself is never kept.
/// </summary>
internal sealed class LikeRateLimiter(TimeProvider time)
{
    public const int Limit = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    // Above this many tracked addresses, expired windows are dropped on the next request.
    private const int PruneAbove = 10_000;

    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> _windows = [];
    private readonly Lock _gate = new();

    /// <summary>
    /// Counts one request from <paramref name="clientIp"/>. False when the address is over the limit in
    /// the current window; <paramref name="retryAfter"/> is then the time until the window ends.
    /// </summary>
    public bool TryAcquire(string clientIp, out TimeSpan retryAfter)
    {
        var key = Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(clientIp)));
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_windows.Count > PruneAbove)
                foreach (var stale in _windows.Where(w => now - w.Value.Start >= Window).Select(w => w.Key).ToList())
                    _windows.Remove(stale);

            if (!_windows.TryGetValue(key, out var window) || now - window.Start >= Window)
                window = (now, 0);
            window.Count++;
            _windows[key] = window;

            retryAfter = window.Start + Window - now;
            return window.Count <= Limit;
        }
    }
}
