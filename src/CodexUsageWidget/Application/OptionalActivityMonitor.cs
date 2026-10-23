namespace CodexUsageWidget.Application;

public sealed class OptionalActivityMonitor(
    Func<bool> isEnabled,
    Func<CodexActivityMonitor> createMonitor) : IAsyncDisposable
{
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private CodexActivityMonitor? _monitor;
    private bool _disposed;

    public event Action<bool>? ActivityChanged;

    public event EventHandler<string>? DiagnosticMessage;

    public async Task RefreshConfigurationAsync(CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (isEnabled())
            {
                if (_monitor is not null)
                {
                    return;
                }

                var monitor = createMonitor();
                monitor.ActivityChanged += OnActivityChanged;
                monitor.DiagnosticMessage += OnDiagnosticMessage;
                try
                {
                    await monitor.StartAsync(cancellationToken).ConfigureAwait(false);
                    _monitor = monitor;
                }
                catch
                {
                    Unsubscribe(monitor);
                    await monitor.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            else
            {
                await StopMonitorAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private void OnActivityChanged(bool active) => ActivityChanged?.Invoke(active);

    private void OnDiagnosticMessage(object? sender, string message) =>
        DiagnosticMessage?.Invoke(this, message);

    private void Unsubscribe(CodexActivityMonitor monitor)
    {
        monitor.ActivityChanged -= OnActivityChanged;
        monitor.DiagnosticMessage -= OnDiagnosticMessage;
    }

    private async Task StopMonitorAsync()
    {
        if (_monitor is not { } monitor)
        {
            return;
        }

        _monitor = null;
        Unsubscribe(monitor);
        await monitor.DisposeAsync().ConfigureAwait(false);
        ActivityChanged?.Invoke(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _transitionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await StopMonitorAsync().ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
        }
    }
}
