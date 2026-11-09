namespace Hmy.Estao.Desktop;

// Runs on the native UI loop; no blocked threads, polling loop or WebView needed.
internal sealed class ProviderRetryTimer(Action retry) : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer = new();
    private DateTimeOffset? _deadline;
    private bool _subscribed;

    internal static int DelayMilliseconds(DateTimeOffset deadline, DateTimeOffset now)
        => (int)Math.Clamp(Math.Ceiling((deadline - now).TotalMilliseconds), 1, int.MaxValue);

    internal void Schedule(DateTimeOffset? deadline)
    {
        _timer.Stop();
        _deadline = deadline;
        if (deadline is null) return;
        if (!_subscribed) { _timer.Tick += Tick; _subscribed = true; }
        _timer.Interval = DelayMilliseconds(deadline.Value, DateTimeOffset.UtcNow);
        _timer.Start();
    }

    private void Tick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_deadline is not { } deadline) return;
        // Very long delays are chunked to fit WinForms' integer interval limit.
        if (DateTimeOffset.UtcNow < deadline) { Schedule(deadline); return; }
        _deadline = null;
        retry();
    }

    public void Dispose() => _timer.Dispose();
}
