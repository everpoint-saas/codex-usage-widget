using CodexUsageWidget.Application;
using CodexUsageWidget.Domain;

namespace CodexUsageWidget.Infrastructure.Codex;

public sealed class CodexUsageProvider : IUsageProvider
{
    private readonly ICodexAppServerSession _session;
    private bool _disposed;
    private volatile bool _includeTokenActivity = true;

    public CodexUsageProvider(ICodexAppServerSession session)
    {
        _session = session;
        _session.NotificationReceived += SessionOnNotificationReceived;
        _session.DiagnosticMessage += SessionOnDiagnosticMessage;
    }

    public event EventHandler? RateLimitsChanged;

    public event EventHandler<string>? DiagnosticMessage;

    public bool IncludeTokenActivity
    {
        get => _includeTokenActivity;
        set => _includeTokenActivity = value;
    }

    public async Task<UsageSnapshot> ReadUsageAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var rateLimitsTask = _session.RequestAsync(
            "account/rateLimits/read",
            parameters: null,
            cancellationToken);
        var tokenActivityTask = IncludeTokenActivity
            ? ReadTokenActivityAsync(cancellationToken)
            : Task.FromResult<TokenActivitySummary?>(null);

        var rateLimits = CodexRateLimitsParser.Parse(
            await rateLimitsTask.ConfigureAwait(false));
        var tokenActivity = await tokenActivityTask.ConfigureAwait(false);

        return new UsageSnapshot(rateLimits, tokenActivity, DateTimeOffset.Now);
    }

    private async Task<TokenActivitySummary?> ReadTokenActivityAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _session.RequestAsync(
                    "account/usage/read",
                    parameters: null,
                    cancellationToken)
                .ConfigureAwait(false);
            return CodexTokenUsageParser.Parse(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticMessage?.Invoke(
                this,
                $"Token activity is unavailable; rate limits remain active. {ex.Message}");
            return null;
        }
    }

    private void SessionOnNotificationReceived(object? sender, string method)
    {
        if (method == "account/rateLimits/updated")
        {
            RateLimitsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SessionOnDiagnosticMessage(object? sender, string message) =>
        DiagnosticMessage?.Invoke(this, message);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.NotificationReceived -= SessionOnNotificationReceived;
        _session.DiagnosticMessage -= SessionOnDiagnosticMessage;
        await _session.DisposeAsync().ConfigureAwait(false);
    }
}
