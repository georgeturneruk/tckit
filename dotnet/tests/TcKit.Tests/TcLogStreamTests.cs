using System.Buffers.Binary;
using System.Text;
using TcKit.Ads;

namespace TcKit.Tests;

/// <summary>The port-100 logger payload has a fixed header and two Latin-1 strings separated by
/// undocumented bytes, so parsing is tested against hand-built buffers — a wrong offset silently
/// corrupts every log line. The subscription wiring needs a live target; what is testable is the
/// ring buffer the notification handler feeds (via the internal Accept seam).</summary>
public sealed class TcLogStreamTests
{
    /// <summary>Build a logger notification payload: 8-byte FILETIME, type mask, sender port at 12,
    /// then sender + message as null-terminated Latin-1 with optional undocumented bytes between.</summary>
    private static byte[] Build(
        DateTime utc, AdsLogType type, ushort port, string sender, string message, byte[]? between = null)
    {
        var buf = new List<byte>();
        Span<byte> fileTime = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(fileTime, utc.ToFileTimeUtc());
        buf.AddRange(fileTime.ToArray());     // 0..7
        buf.Add((byte)type);                  // 8
        buf.AddRange(new byte[] { 0, 0, 0 }); // 9..11 unknown
        Span<byte> portBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(portBytes, port);
        buf.AddRange(portBytes.ToArray());    // 12..13
        buf.AddRange(new byte[] { 0, 0 });    // 14..15 unknown
        buf.AddRange(Encoding.Latin1.GetBytes(sender));
        buf.Add(0);
        if (between is not null)
        {
            buf.AddRange(between);
            buf.Add(0);
        }

        buf.AddRange(Encoding.Latin1.GetBytes(message));
        buf.Add(0);
        return [.. buf];
    }

    private static AdsLogEntry Line(string message, AdsLogType type = AdsLogType.Log, int minutesAgo = 0) => new()
    {
        TimestampUtc = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc).AddMinutes(-minutesAgo),
        Type = type,
        SourceAdsPort = 851,
        Source = "TCOM Server",
        Message = message,
    };

    [Fact]
    public void Parse_HeaderAndStrings_WithUndocumentedBytesBetween()
    {
        var when = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        var data = Build(when, AdsLogType.Error, 851, "TCOM Server", "Pump overtemp", between: [0xAB, 0xCD]);

        var entry = AdsLogEntry.Parse(data);

        Assert.Equal(when, entry.TimestampUtc);
        Assert.Equal(AdsLogType.Error, entry.Type);
        Assert.Equal(AdsLogSeverity.Error, entry.Severity);
        Assert.Equal((ushort)851, entry.SourceAdsPort);
        Assert.Equal("TCOM Server", entry.Source);
        Assert.Equal("Pump overtemp", entry.Message);
    }

    [Fact]
    public void Parse_SimpleCase_NoBytesBetween()
    {
        // Only one null between sender and message: first == last null.
        var entry = AdsLogEntry.Parse(Build(DateTime.UtcNow, AdsLogType.Log, 851, "Src", "Msg"));

        Assert.Equal("Src", entry.Source);
        Assert.Equal("Msg", entry.Message);
    }

    [Theory]
    [InlineData(AdsLogType.Error, AdsLogSeverity.Error)]
    [InlineData(AdsLogType.Warning, AdsLogSeverity.Warning)]
    [InlineData(AdsLogType.Hint, AdsLogSeverity.Info)]
    [InlineData(AdsLogType.Log, AdsLogSeverity.Info)]
    public void Severity_DerivesFromTypeMask(AdsLogType type, AdsLogSeverity expected)
        => Assert.Equal(expected, AdsLogEntry.Parse(Build(DateTime.UtcNow, type, 1, "s", "m")).Severity);

    [Fact]
    public void Severity_ErrorBitWinsOverWarningBit()
    {
        var entry = AdsLogEntry.Parse(
            Build(DateTime.UtcNow, AdsLogType.Error | AdsLogType.Warning, 1, "s", "m"));
        Assert.Equal(AdsLogSeverity.Error, entry.Severity);
    }

    [Fact]
    public void Parse_TooShortBuffer_YieldsEmptyEntry()
    {
        var entry = AdsLogEntry.Parse(new byte[4]);

        Assert.Equal("", entry.Source);
        Assert.Equal("", entry.Message);
        Assert.Equal(default, entry.TimestampUtc);
    }

    [Fact]
    public void Snapshot_IsNewestFirst()
    {
        using var stream = new TcLogStream("1.2.3.4.1.1");
        stream.Accept(Line("first", minutesAgo: 2));
        stream.Accept(Line("second", minutesAgo: 1));
        stream.Accept(Line("third"));

        Assert.Equal(["third", "second", "first"], stream.Snapshot().Select(e => e.Message));
    }

    [Fact]
    public void Buffer_IsCappedDroppingOldest()
    {
        using var stream = new TcLogStream("1.2.3.4.1.1", capacity: 3);
        for (var i = 0; i < 5; i++)
        {
            stream.Accept(Line($"line {i}"));
        }

        Assert.Equal(["line 4", "line 3", "line 2"], stream.Snapshot().Select(e => e.Message));
    }

    [Fact]
    public void EachLine_RaisesTheEvent()
    {
        using var stream = new TcLogStream("1.2.3.4.1.1");
        var seen = new List<string>();
        stream.LogReceived += e => seen.Add(e.Message);

        stream.Accept(Line("hello"));

        Assert.Equal(["hello"], seen);
    }

    [Fact]
    public void EmptyEntryFromTooShortPayload_IsDropped()
    {
        using var stream = new TcLogStream("1.2.3.4.1.1");
        var raised = false;
        stream.LogReceived += _ => raised = true;

        stream.Accept(AdsLogEntry.Parse(new byte[4])); // parses to the all-default entry

        Assert.Empty(stream.Snapshot());
        Assert.False(raised);
    }

    [Fact]
    public void ThrowingConsumer_DoesNotPoisonTheStream()
    {
        using var stream = new TcLogStream("1.2.3.4.1.1");
        stream.LogReceived += _ => throw new InvalidOperationException("bad consumer");

        stream.Accept(Line("survives"));
        stream.Accept(Line("still going"));

        Assert.Equal(2, stream.Snapshot().Count);
    }
}
