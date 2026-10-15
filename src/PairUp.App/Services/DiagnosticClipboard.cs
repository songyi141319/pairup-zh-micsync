using System.Runtime.InteropServices;

namespace PairUp.App.Services;

internal enum DiagnosticCopyStatus { Verified, Unconfirmed, Failed }
internal sealed record DiagnosticCopyResult(DiagnosticCopyStatus Status, int Characters);

internal static class DiagnosticClipboard
{
    // Delegates keep regression checks away from the user's actual clipboard.
    internal static async Task<DiagnosticCopyResult> CopyAsync(string text, Action<string> write,
        Func<string> read, Func<Task>? wait = null)
    {
        wait ??= () => Task.Delay(100);
        var wroteWithoutError = false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0) await wait(); // Preserve the caller's STA/UI context.
            if (!wroteWithoutError)
            {
                try { write(text); wroteWithoutError = true; }
                catch (ExternalException) { /* Writing may succeed before the flush fails. */ }
            }
            try
            {
                if (string.Equals(read(), text, StringComparison.Ordinal))
                    return new(DiagnosticCopyStatus.Verified, text.Length);
                // A successful write followed by different text can be another application
                // or user copy. Do not overwrite it repeatedly or claim a complete copy.
                if (wroteWithoutError) return new(DiagnosticCopyStatus.Unconfirmed, text.Length);
            }
            catch (ExternalException) { /* Clipboard may be briefly held by another process. */ }
        }
        return new(wroteWithoutError ? DiagnosticCopyStatus.Unconfirmed : DiagnosticCopyStatus.Failed, text.Length);
    }
}
