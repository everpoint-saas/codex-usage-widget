using System.Text.Json;
using CodexUsageWidget.Infrastructure.Codex;

namespace CodexUsageWidget.Tests;

public sealed class CodexUsageProviderTests
{
    [Fact]
    public async Task RateLimitOnlyReadKeepsWeeklyLimitAndResetCreditsWithoutTokenRequest()
    {
        await using var session = new FakeCodexAppServerSession("""
            {
              "rateLimits": {
                "limitId": "codex",
                "primary": { "usedPercent": 32, "windowDurationMins": 10080 }
              },
              "rateLimitResetCredits": { "availableCount": 2 }
            }
            """);
        await using var provider = new CodexUsageProvider(session) { IncludeTokenActivity = false };

        var snapshot = await provider.ReadUsageAsync();

        Assert.Equal(68, Assert.Single(snapshot.GeneralWindows).RemainingPercent);
        Assert.Equal(2, snapshot.RateLimits.ResetCredits?.AvailableCount);
        Assert.Null(snapshot.TokenActivity);
        Assert.Equal(["account/rateLimits/read"], session.Requests);

        provider.IncludeTokenActivity = true;
        await provider.ReadUsageAsync();
        Assert.Contains("account/usage/read", session.Requests);

        session.Requests.Clear();
        provider.IncludeTokenActivity = false;
        await provider.ReadUsageAsync();
        Assert.Equal(["account/rateLimits/read"], session.Requests);
    }

    [Fact]
    public async Task ReadUsageKeepsRateLimitsWhenTokenActivityFails()
    {
        await using var session = new FakeCodexAppServerSession(
            """{ "rateLimits": { "limitId": "codex", "primary": { "usedPercent": 30 } } }""",
            tokenUsageError: new InvalidOperationException("not supported"));
        await using var provider = new CodexUsageProvider(session);
        string? diagnostic = null;
        provider.DiagnosticMessage += (_, message) => diagnostic = message;

        var snapshot = await provider.ReadUsageAsync();

        Assert.Single(snapshot.GeneralWindows);
        Assert.Null(snapshot.TokenActivity);
        Assert.Contains("rate limits remain active", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProviderForwardsRateLimitNotificationsOnly()
    {
        await using var session = new FakeCodexAppServerSession(
            """{ "rateLimits": { "limitId": "codex", "primary": { "usedPercent": 30 } } }""");
        await using var provider = new CodexUsageProvider(session);
        var notifications = 0;
        provider.RateLimitsChanged += (_, _) => notifications++;

        session.RaiseNotification("thread/updated");
        session.RaiseNotification("account/rateLimits/updated");

        Assert.Equal(1, notifications);
    }

    private sealed class FakeCodexAppServerSession : ICodexAppServerSession
    {
        private readonly JsonElement _rateLimits;
        private readonly JsonElement _tokenUsage;
        private readonly Exception? _tokenUsageError;

        public List<string> Requests { get; } = [];

        public FakeCodexAppServerSession(string rateLimits, Exception? tokenUsageError = null)
        {
            _rateLimits = ParseAndClone(rateLimits);
            _tokenUsage = ParseAndClone("""{ "summary": {} }""");
            _tokenUsageError = tokenUsageError;
        }

        public event EventHandler<string>? NotificationReceived;

        public event EventHandler<string>? DiagnosticMessage
        {
            add { }
            remove { }
        }

        public Task<JsonElement> RequestAsync(
            string method,
            object? parameters,
            CancellationToken cancellationToken)
        {
            _ = parameters;
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(method);
            if (method == "account/rateLimits/read")
            {
                return Task.FromResult(_rateLimits);
            }

            if (_tokenUsageError is not null)
            {
                return Task.FromException<JsonElement>(_tokenUsageError);
            }

            return Task.FromResult(_tokenUsage);
        }

        public void RaiseNotification(string method) =>
            NotificationReceived?.Invoke(this, method);

        public ValueTask DisposeAsync()
        {
            NotificationReceived = null;
            return ValueTask.CompletedTask;
        }

        private static JsonElement ParseAndClone(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }
}
