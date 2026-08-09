namespace TcKit.Adapters.Xml;

/// <summary>
/// The object-Id GUID source for new tree items. Production uses <see cref="Guid.NewGuid"/>
/// (uniqueness is the only constraint XAE places on Ids); tests pin <see cref="Next"/> to make
/// new-file emission byte-for-byte assertable. The hook is static process state, so tests that
/// pin it live in a single test class (the ParameterGuard rule).
/// </summary>
internal static class GuidSource
{
    internal static Func<Guid> Next { get; set; } = Guid.NewGuid;

    /// <summary>A fresh Id in XAE's on-disk shape: lowercase, brace-wrapped.</summary>
    public static string NewId() => Next().ToString("B");
}
