using System.Runtime.InteropServices;

namespace TcKit.Adapters.Automation;

/// <summary>
/// Deterministic release for a COM runtime-callable wrapper. Disposing releases the
/// underlying COM reference rather than waiting for the GC, which is the rule for all
/// COM interop in TcKit (see CONVENTIONS.md). Wrap every Automation Interface object
/// in a <c>using</c> over one of these.
/// </summary>
public sealed class ComScope : IDisposable
{
    private object? _comObject;

    public ComScope(object comObject) => _comObject = comObject;

    /// <summary>The wrapped COM object. Throws once the scope has been disposed.</summary>
    public object Object => _comObject ?? throw new ObjectDisposedException(nameof(ComScope));

    public void Dispose()
    {
        if (_comObject is not null && Marshal.IsComObject(_comObject))
        {
            Marshal.FinalReleaseComObject(_comObject);
        }

        _comObject = null;
    }
}
