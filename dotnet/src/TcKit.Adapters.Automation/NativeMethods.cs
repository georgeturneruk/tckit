using System.Runtime.InteropServices;

namespace TcKit.Adapters.Automation;

/// <summary>
/// P/Invoke shim for attaching to a running COM server by ProgID. <c>Marshal.GetActiveObject</c>
/// was removed from .NET Core/8, so net8 must call the underlying <c>GetActiveObject</c> (oleaut32)
/// plus <c>CLSIDFromProgID</c> (ole32) directly. Proven against live 4026 in the Phase-0 spike.
/// </summary>
internal static class NativeMethods
{
    [DllImport("ole32.dll", PreserveSig = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string progId, out Guid clsid);

    [DllImport("oleaut32.dll", PreserveSig = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetActiveObject(ref Guid rclsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object obj);

    /// <summary>Return the running COM object registered under <paramref name="progId"/>.</summary>
    public static object GetActiveObject(string progId)
    {
        var hr = CLSIDFromProgID(progId, out var clsid);
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        hr = GetActiveObject(ref clsid, IntPtr.Zero, out var obj);
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        return obj;
    }
}
