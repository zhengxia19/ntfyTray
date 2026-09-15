using NtfyTray.Notifications;

namespace NtfyTray.Tests;

public class NotificationProcessorTests
{
    private static readonly FormattedNotification Message = new("title", "private body");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledStillChecksSettingsAndPreservesSystemFault(bool allowed)
    {
        var reads = 0;
        var processor = new NotificationProcessor(false, true,
            _ => { reads++; return Task.FromResult(allowed); },
            (_, _) => throw new Xunit.Sdk.XunitException("Must not submit"));
        await processor.InitializeAsync();
        Assert.Equal(NotificationResult.Suppressed, await processor.SubmitAsync(Message, default));
        Assert.Equal(2, reads);
        Assert.Equal(new NotificationFaults(null, allowed ? null : NotificationFailureCode.SettingsRestricted, null), processor.Faults);
    }

    [Theory]
    [InlineData(0, NotificationResult.Submitted, 1)]
    [InlineData(1, NotificationResult.Submitted, 2)]
    [InlineData(2, NotificationResult.Submitted, 3)]
    [InlineData(3, NotificationResult.Dropped, 3)]
    public async Task BoundedAttempts(int failures, NotificationResult expected, int expectedAttempts)
    {
        var attempts = 0;
        var delays = 0;
        var processor = new NotificationProcessor(true, true, _ => Task.FromResult(true),
            (_, _) => ++attempts <= failures ? Task.FromException(new Exception("private body")) : Task.CompletedTask,
            (duration, token) =>
            {
                Assert.Equal(TimeSpan.FromMilliseconds(200), duration);
                delays++;
                return Task.CompletedTask;
            });
        Assert.Equal(expected, await processor.SubmitAsync(Message, default));
        Assert.Equal(expectedAttempts, attempts);
        Assert.Equal(expectedAttempts - 1, delays);
        Assert.Equal(expected == NotificationResult.Dropped ? NotificationFailureCode.SubmissionFailed : null,
            processor.Faults.Submission);
        Assert.DoesNotContain("private body", processor.Faults.ToString());
    }

    [Fact]
    public async Task SettingsRecoveryDoesNotClearSubmissionFailureAndSuppressionDoesNotClaimRecovery()
    {
        var allowed = true;
        var fail = true;
        var processor = new NotificationProcessor(true, true, _ => Task.FromResult(allowed),
            (_, _) => fail ? Task.FromException(new Exception()) : Task.CompletedTask,
            (_, _) => Task.CompletedTask);
        Assert.Equal(NotificationResult.Dropped, await processor.SubmitAsync(Message, default));
        allowed = false;
        Assert.Equal(NotificationResult.Suppressed, await processor.SubmitAsync(Message, default));
        Assert.Equal(NotificationFailureCode.SettingsRestricted, processor.Faults.Settings);
        Assert.Equal(NotificationFailureCode.SubmissionFailed, processor.Faults.Submission);
        allowed = true;
        await processor.InitializeAsync();
        Assert.Null(processor.Faults.Settings);
        Assert.Equal(NotificationFailureCode.SubmissionFailed, processor.Faults.Submission);
        fail = false;
        Assert.Equal(NotificationResult.Submitted, await processor.SubmitAsync(Message, default));
        Assert.Null(processor.Faults.Submission);
    }

    [Fact]
    public async Task InitializationFailurePersistsAfterSettingsRecovery()
    {
        var allowed = false;
        var processor = new NotificationProcessor(true, false, _ => Task.FromResult(allowed),
            (_, _) => throw new Xunit.Sdk.XunitException("Must not submit"));
        await processor.InitializeAsync();
        allowed = true;
        Assert.Equal(NotificationResult.Dropped, await processor.SubmitAsync(Message, default));
        Assert.Null(processor.Faults.Settings);
        Assert.Equal(NotificationFailureCode.InitializationFailed, processor.Faults.Initialization);
    }

    [Theory]
    [InlineData(true, NotificationResult.Dropped)]
    [InlineData(false, NotificationResult.Suppressed)]
    public async Task SettingsReadFailureIsSafe(bool enabled, NotificationResult expected)
    {
        var submits = 0;
        var processor = new NotificationProcessor(enabled, true,
            _ => Task.FromException<bool>(new Exception("secret token")),
            (_, _) => { submits++; return Task.CompletedTask; });
        Assert.Equal(expected, await processor.SubmitAsync(Message, default));
        Assert.Equal(0, submits);
        Assert.Equal(NotificationFailureCode.SettingsReadFailed, processor.Faults.Settings);
        Assert.DoesNotContain("secret token", processor.Faults.ToString());
    }

    [Theory]
    [InlineData("before")]
    [InlineData("settings")]
    [InlineData("submit")]
    [InlineData("delay")]
    public async Task CancellationPropagatesWithoutFailureResult(string stage)
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        var processor = new NotificationProcessor(true, true,
            token =>
            {
                if (stage == "settings") cancellation.Cancel();
                return Task.FromResult(true);
            },
            (_, token) =>
            {
                attempts++;
                if (stage == "submit") cancellation.Cancel();
                return stage == "delay" ? Task.FromException(new Exception()) : Task.CompletedTask;
            },
            (_, token) => { cancellation.Cancel(); return Task.FromCanceled(token); });
        if (stage == "before") cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.SubmitAsync(Message, cancellation.Token));
        Assert.Null(processor.Faults.Submission);
        Assert.True(attempts <= 1);
    }

    [Fact]
    public async Task CancellationExceptionFromDelegateIsNotRetried()
    {
        var attempts = 0;
        var processor = new NotificationProcessor(true, true, _ => Task.FromResult(true),
            (_, _) => { attempts++; throw new OperationCanceledException(); });
        await Assert.ThrowsAsync<OperationCanceledException>(() => processor.SubmitAsync(Message, default));
        Assert.Equal(1, attempts);
        Assert.Null(processor.Faults.Submission);
    }
}
