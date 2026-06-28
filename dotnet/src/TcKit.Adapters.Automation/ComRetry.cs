using System.Runtime.InteropServices;

namespace TcKit.Adapters.Automation;

/// <summary>
/// Retries a COM call that the XAE STA rejects while busy. The Automation Interface raises
/// RPC_E_CALL_REJECTED (0x80010001) or RPC_E_RETRYLATER (0x8001010A) when the IDE is mid-operation;
/// the documented remedy is a short backoff and retry. Port of the bridge's <c>Invoke-WithComRetry</c>.
/// Runs on the STA worker thread, so the blocking sleep is on that thread alone.
/// </summary>
internal static class ComRetry
{
    private const int RpcCallRejected = unchecked((int)0x80010001);
    private const int RpcRetryLater = unchecked((int)0x8001010A);

    public static T Invoke<T>(Func<T> action, int maxAttempts = 6, int baseDelayMs = 200)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (COMException ex)
                when ((ex.HResult == RpcCallRejected || ex.HResult == RpcRetryLater) && attempt < maxAttempts)
            {
                Thread.Sleep(baseDelayMs * (int)Math.Pow(2, attempt - 1));
            }
        }
    }

    public static void Invoke(Action action, int maxAttempts = 6, int baseDelayMs = 200)
        => Invoke<object?>(() => { action(); return null; }, maxAttempts, baseDelayMs);
}
