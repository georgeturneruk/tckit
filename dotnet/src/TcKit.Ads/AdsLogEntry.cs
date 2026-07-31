using System.Buffers.Binary;
using System.Text;

namespace TcKit.Ads;

/// <summary>Message-type bitmask of a TwinCAT router log line (byte 8 of the port-100 notification).</summary>
[Flags]
public enum AdsLogType : byte
{
    None = 0,
    Hint = 0x01,
    Warning = 0x02,
    Error = 0x04,
    Log = 0x10,
    MsgBox = 0x20,
    Resource = 0x40,
    String = 0x80,
}

/// <summary>Coarse severity derived from <see cref="AdsLogType"/>.</summary>
public enum AdsLogSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>One line from the TwinCAT system/router logger (ADS Logger, AMS port 100): system
/// service messages, licence warnings, and PLC <c>ADSLOGSTR</c> output.</summary>
public sealed record AdsLogEntry
{
    public DateTime TimestampUtc { get; init; }
    public AdsLogType Type { get; init; }
    public ushort SourceAdsPort { get; init; }
    public string Source { get; init; } = "";
    public string Message { get; init; } = "";

    public AdsLogSeverity Severity =>
        Type.HasFlag(AdsLogType.Error) ? AdsLogSeverity.Error
        : Type.HasFlag(AdsLogType.Warning) ? AdsLogSeverity.Warning
        : AdsLogSeverity.Info;

    /// <summary>Parse one port-100 logger notification payload. Layout:
    /// [0..7] FILETIME, [8] type mask, [12..13] sender ADS port (LE), then from [16] two
    /// null-terminated Latin-1 strings — sender (first) and message (last), with a few undocumented
    /// bytes between them. Total and never throws; a too-short buffer yields an empty entry.</summary>
    public static AdsLogEntry Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16)
        {
            return new AdsLogEntry();
        }

        var entry = new AdsLogEntry
        {
            TimestampUtc = ToUtc(BinaryPrimitives.ReadInt64LittleEndian(data)),
            Type = (AdsLogType)data[8],
            SourceAdsPort = BinaryPrimitives.ReadUInt16LittleEndian(data[12..]),
        };

        // Strings from offset 16. Sender is the first null-terminated token; message is the last
        // one (undocumented bytes may sit between them).
        var text = Encoding.Latin1.GetString(data[16..]).TrimEnd('\0');
        var firstNull = text.IndexOf('\0', StringComparison.Ordinal);
        var lastNull = text.LastIndexOf('\0');

        return entry with
        {
            Source = firstNull >= 0 ? text[..firstNull] : text,
            Message = lastNull >= 0 ? text[(lastNull + 1)..] : text,
        };
    }

    private static DateTime ToUtc(long fileTime)
    {
        try
        {
            return fileTime > 0 ? DateTime.FromFileTimeUtc(fileTime) : default;
        }
        catch (ArgumentOutOfRangeException)
        {
            return default;
        }
    }
}
