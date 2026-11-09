using System.Net;
using System.Security.Cryptography;
using System.Text;
using Hmy.Estao.Core.Models;

namespace Hmy.Estao.Core.Providers;

// Shared by a factory's providers, including settings authentication checks.
// Waiting is represented by a deadline, never a sleeping HTTP request.
internal sealed class ClaudeUsageRetryGate(TimeProvider clock)
{
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, UsageSnapshot> _blocked = [];
    private string? _active;
    private long _generation;
    internal event EventHandler? Changed;
    internal DateTimeOffset UtcNow => clock.GetUtcNow();
    internal string? CredentialIdentity { get { lock (_gate) return _active; } }
    internal DateTimeOffset? RetryAt
    {
        get { lock (_gate) return _active is not null && _blocked.TryGetValue(_active, out var state) ? state.RetryAt : null; }
    }

    internal bool TakeDueRetry()
    {
        lock (_gate)
        {
            if (_active is null || !_blocked.TryGetValue(_active, out var state) || state.RetryAt > UtcNow) return false;
            _blocked.Remove(_active);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal void Clear()
    {
        lock (_gate) { _blocked.Clear(); _active = null; _generation++; }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal UsageSnapshot? GetDeferred(string credential)
    {
        var key = Fingerprint(credential);
        UsageSnapshot? blocked;
        bool changed;
        lock (_gate)
        {
            if (!_blocked.TryGetValue(key, out blocked)) return null;
            if (blocked.RetryAt <= UtcNow) { _blocked.Remove(key); return null; }
            changed = _active != key;
            _active = key;
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
        return blocked;
    }

    private static string Fingerprint(string credential) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));

    internal async Task<UsageSnapshot> ExecuteAsync(string credential, Func<Task<UsageSnapshot>> fetch, CancellationToken ct)
    {
        var key = Fingerprint(credential);
        await _requests.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            UsageSnapshot? blocked;
            bool changed;
            long generation;
            lock (_gate)
            {
                changed = _active != key;
                _active = key;
                _blocked.TryGetValue(key, out blocked);
                generation = _generation;
                if (blocked?.RetryAt <= UtcNow) { _blocked.Remove(key); blocked = null; changed = true; }
            }
            if (changed) Changed?.Invoke(this, EventArgs.Empty);
            if (blocked?.RetryAt > UtcNow) return blocked;
            var result = await fetch().ConfigureAwait(false);
            lock (_gate)
            {
                if (generation != _generation) return result with { RetryAt = null };
                if (result.RetryAt is not null) _blocked[key] = result;
                else _blocked.Remove(key);
            }
            Changed?.Invoke(this, EventArgs.Empty);
            return result;
        }
        finally { _requests.Release(); }
    }

    internal UsageSnapshot Failure(HttpResponseMessage response, string source)
    {
        DateTimeOffset? retryAt = null;
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var header = response.Headers.RetryAfter;
            if (header?.Delta is { } delta && delta > TimeSpan.Zero && delta <= DateTimeOffset.MaxValue - UtcNow)
                retryAt = UtcNow + delta;
            else if (header?.Date is { } date && date > UtcNow) retryAt = date;
        }
        return UsageSnapshot.Failure("claude", source, $"Claude usage API returned {(int)response.StatusCode}.",
            response.StatusCode == HttpStatusCode.TooManyRequests ? new("error.claude_rate_limited")
                : new("error.http", "Claude", (int)response.StatusCode)) with { RetryAt = retryAt };
    }
}
