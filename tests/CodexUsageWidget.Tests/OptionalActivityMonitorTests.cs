using CodexUsageWidget.Application;

namespace CodexUsageWidget.Tests;

public sealed class OptionalActivityMonitorTests
{
    [Fact]
    public async Task DisabledHooksDoNotCreateOrStartMonitoring()
    {
        var created = 0;
        await using var monitor = new OptionalActivityMonitor(() => false, () =>
        {
            created++;
            return new CodexActivityMonitor(new FakeSignalSource());
        });

        await monitor.RefreshConfigurationAsync();
        await monitor.RefreshConfigurationAsync();

        Assert.Equal(0, created);
    }

    [Fact]
    public async Task InstallingAndRemovingHooksStartsStopsAndCanRestartMonitoring()
    {
        var enabled = true;
        var sources = new List<FakeSignalSource>();
        await using var monitor = new OptionalActivityMonitor(() => enabled, () =>
        {
            var source = new FakeSignalSource();
            sources.Add(source);
            return new CodexActivityMonitor(source);
        });
        var changes = new List<bool>();
        monitor.ActivityChanged += changes.Add;

        await monitor.RefreshConfigurationAsync();
        await monitor.RefreshConfigurationAsync();
        var first = Assert.Single(sources);
        Assert.True(first.Started);
        first.Publish(new(CodexActivitySignalKind.TurnStarted, "session", "turn"));
        Assert.Equal([true], changes);

        enabled = false;
        await monitor.RefreshConfigurationAsync();
        Assert.True(first.Disposed);
        Assert.Equal([true, false], changes);
        first.Publish(new(CodexActivitySignalKind.TurnStarted, "session", "stale"));
        Assert.Equal([true, false], changes);

        enabled = true;
        await monitor.RefreshConfigurationAsync();
        Assert.Equal(2, sources.Count);
        Assert.True(sources[1].Started);
    }

    [Fact]
    public async Task StartupFailureDisposesSourceAndAllowsRetry()
    {
        var failed = new FakeSignalSource { FailStart = true };
        var healthy = new FakeSignalSource();
        var attempts = 0;
        await using var monitor = new OptionalActivityMonitor(() => true, () =>
            new CodexActivityMonitor(++attempts == 1 ? failed : healthy));

        await Assert.ThrowsAsync<InvalidOperationException>(() => monitor.RefreshConfigurationAsync());
        Assert.True(failed.Disposed);
        await monitor.RefreshConfigurationAsync();
        Assert.True(healthy.Started);
    }

    private sealed class FakeSignalSource : ICodexActivitySignalSource
    {
        public event Action<CodexActivitySignal>? SignalReceived;

        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public bool FailStart { get; init; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailStart)
            {
                throw new InvalidOperationException("Start failed");
            }

            Started = true;
            return Task.CompletedTask;
        }

        public void Publish(CodexActivitySignal signal) => SignalReceived?.Invoke(signal);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            SignalReceived = null;
            return ValueTask.CompletedTask;
        }
    }
}
