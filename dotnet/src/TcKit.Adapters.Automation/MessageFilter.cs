using System.Runtime.InteropServices;

namespace TcKit.Adapters.Automation;

/// <summary>
/// An <c>IOleMessageFilter</c> for the STA thread that drives TcXaeShell. When an outgoing COM call
/// is rejected because the IDE is busy (RPC_E_CALL_REJECTED / SERVERCALL_RETRYLATER), COM invokes
/// <see cref="RetryRejectedCall"/>; returning a small delay makes COM retry the call with proper
/// message pumping instead of failing immediately. This is the canonical Visual Studio / TwinCAT
/// automation fix (lifted from the TcUnit-Runner pattern, per ADR-0015) and is far more robust than
/// catching the exception after COM has already given up.
/// </summary>
internal sealed class MessageFilter : IOleMessageFilter
{
    private const int Handled = 0;            // SERVERCALL_ISHANDLED
    private const int RetryLater = 2;         // SERVERCALL_RETRYLATER
    private const int WaitAndDispatch = 2;    // PENDINGMSG_WAITDEFPROCESS
    private const int Cancel = -1;
    private const int RetryDelayMs = 150;
    private const int GiveUpAfterMs = 60_000;

    /// <summary>Register the filter on the current (STA) thread. Must be called from that thread.</summary>
    public static void Register() => CoRegisterMessageFilter(new MessageFilter(), out _);

    public int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo)
        => Handled;

    public int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType)
    {
        // Retry busy calls (with a short wait) until a generous deadline, then give up.
        if (dwRejectType == RetryLater && dwTickCount < GiveUpAfterMs)
        {
            return RetryDelayMs;
        }

        return Cancel;
    }

    public int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType) => WaitAndDispatch;

    [DllImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CoRegisterMessageFilter(IOleMessageFilter newFilter, out IOleMessageFilter? oldFilter);
}

[ComImport]
[Guid("00000016-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleMessageFilter
{
    [PreserveSig]
    int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);

    [PreserveSig]
    int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);

    [PreserveSig]
    int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
}
