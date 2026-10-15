namespace PairUp.App.Services;

/// <summary>Silent connection startup, separate from audible measurement.</summary>
internal static class CalibrationStartup
{
    internal static async Task PrepareAsync(Action startSilent, Action applyLevel, Action verify,
        CancellationToken cancellationToken, Func<int, CancellationToken, Task>? delay = null,
        Action? retryStartupRollback = null)
    {
        delay ??= Task.Delay;
        cancellationToken.ThrowIfCancellationRequested();
        startSilent();
        await delay(600, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        applyLevel();
        var retried = false;
        // Four observations over 800ms, while the provider is still silent. Restart the
        // observation window once only for a confirmed return to the lower old level.
        for (var stable = 0; stable < 4;)
        {
            await delay(200, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            try { verify(); stable++; }
            catch (CalibrationStartupRollbackException) when (!retried && retryStartupRollback is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                retried = true;
                retryStartupRollback();
                stable = 0;
            }
        }
    }

    internal static void RetryLevel(Action verifyEndpoints, Action verifySessions, Action apply, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        verifyEndpoints();
        verifySessions();
        token.ThrowIfCancellationRequested();
        apply();
    }
}

internal sealed class CalibrationStartupRollbackException(string message) : InvalidOperationException(message);
