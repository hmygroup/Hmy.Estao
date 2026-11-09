namespace Hmy.Estao.Desktop.Tests;

public sealed class ProviderRetryTimerTests
{
    [Fact]
    public void ArbitraryDeadlinesAreNotRoundedToTheConfiguredRefreshInterval()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(123456, ProviderRetryTimer.DelayMilliseconds(now.AddMilliseconds(123456), now));
        Assert.Equal(1, ProviderRetryTimer.DelayMilliseconds(now.AddSeconds(-1), now));
        Assert.Equal(int.MaxValue, ProviderRetryTimer.DelayMilliseconds(now.AddDays(90), now));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeTimerFiresOnceAtTheDeadlineAndCanBeCancelled(bool cancel)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new System.Windows.Forms.Form { ShowInTaskbar = false, Opacity = 0 };
                var deadline = DateTimeOffset.UtcNow.AddMilliseconds(175);
                var calls = 0;
                using var timer = new ProviderRetryTimer(() =>
                {
                    calls++;
                    if (DateTimeOffset.UtcNow < deadline) failure = new Exception("Retried before Retry-After elapsed.");
                });
                using var stop = new System.Windows.Forms.Timer { Interval = 450 };
                stop.Tick += (_, _) => form.Close();
                form.Shown += (_, _) =>
                {
                    timer.Schedule(deadline);
                    if (cancel) timer.Schedule(null);
                    stop.Start();
                };
                System.Windows.Forms.Application.Run(form);
                Assert.Equal(cancel ? 0 : 1, calls);
            }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
