using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using TwinCAT.Ads;

namespace TcKit.Ads;

/// <summary>One TwinCAT licence on a target, from the licence server (AMS port 30).
/// <see cref="StatusCode"/> is the licence HRESULT: 0 = OK, 0x203 = OK pending device validation,
/// 0x254 = <b>trial</b>, 0x255 = OEM. A trial's <see cref="Expiration"/> is the wall-clock deadline
/// after which the runtime stops reaching Run mode — the classic silent commissioning staller.</summary>
public sealed record TcLicenseInfo
{
    public Guid LicenseId { get; init; }
    public string? Name { get; init; }

    /// <summary>Expiry as UTC; null when the entry carries no expiry (permanent).</summary>
    public DateTimeOffset? Expiration { get; init; }
    public uint StatusCode { get; init; }

    /// <summary>Licensed instance count; 0 = unlimited.</summary>
    public uint InstanceCount { get; init; }

    public bool IsTrial => StatusCode == 0x254;
    public bool IsValid => StatusCode is 0 or 0x203 or 0x254 or 0x255;
}

/// <summary>
/// Reads a target's licence inventory from the TwinCAT licence server (AMS port 30): entry list at
/// IG 0x01010006 (valid) / 0x0101000A (invalid), 48 bytes per entry (GUID @0, FILETIME expiry @16,
/// HRESULT u32 @24, instance count u32 @28); the human-readable name resolves via ReadWrite
/// IG 0x0101000C with the licence GUID as the write payload. Owns its <see cref="AdsClient"/>.
///
/// The preflight (<see cref="DiagnoseStuckInConfig(string)"/>) answers the failure mode where
/// Deploy reports success but the runtime bounces back to Config with no stated reason: an expired
/// (trial) licence. Renewal is an interactive XAE dialog, so automation can only name the cause.
/// </summary>
public sealed class TcLicenses : IDisposable
{
    private const int LicenseServerPort = 30; // AMSPORT_R0_LICENSESERVER
    private const uint ValidLicenses = 0x01010006;
    private const uint InvalidLicenses = 0x0101000A;
    private const uint LicenseName = 0x0101000C;
    private const int EntryBytes = 48;

    private readonly AdsClient _client = new();
    private readonly AmsNetId _netId;

    public TcLicenses(string netId) => _netId = new AmsNetId(netId);

    /// <summary>Read all licences the server reports: valid first, then invalid
    /// (expired trials show up in the invalid list — the interesting ones for diagnosis).</summary>
    public IReadOnlyList<TcLicenseInfo> Read()
    {
        if (!_client.IsConnected)
        {
            _client.Connect(_netId, LicenseServerPort);
        }

        var licenses = new List<TcLicenseInfo>();
        licenses.AddRange(ReadList(ValidLicenses));
        licenses.AddRange(ReadList(InvalidLicenses));
        return licenses;
    }

    /// <summary>
    /// One-line diagnosis when expired licences explain a target stuck in Config; null when the
    /// licences don't explain it (or the licence server is unreachable — never throws).
    /// </summary>
    public static string? DiagnoseStuckInConfig(string netId)
    {
        try
        {
            using var reader = new TcLicenses(netId);
            return DiagnoseStuckInConfig(reader.Read(), DateTimeOffset.UtcNow);
        }
#pragma warning disable CA1031 // A preflight must never turn a diagnosis attempt into a new failure.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>Pure diagnosis over an already-read inventory (unit-testable half).</summary>
    public static string? DiagnoseStuckInConfig(IReadOnlyList<TcLicenseInfo> licenses, DateTimeOffset nowUtc)
    {
        var expired = licenses
            .Where(l => l.Expiration is { } expiry && expiry <= nowUtc)
            .OrderBy(l => l.Expiration)
            .ToList();
        if (expired.Count == 0)
        {
            return null;
        }

        var worst = expired[0];
        var name = string.IsNullOrEmpty(worst.Name) ? worst.LicenseId.ToString() : worst.Name;
        var kind = worst.IsTrial ? "trial licence" : "licence";
        var date = worst.Expiration!.Value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return $"The target's {kind} '{name}' expired on {date}; an expired licence keeps the runtime "
            + "in Config mode. Renew it in TcXaeShell (System > License > Order Information, "
            + "'7 Days Trial License') — the dialog is interactive and cannot be automated.";
    }

    /// <summary>Parse one 48-byte entry (name resolved separately).</summary>
    public static TcLicenseInfo Parse(ReadOnlySpan<byte> entry)
    {
        var fileTime = BinaryPrimitives.ReadInt64LittleEndian(entry[16..]);
        return new TcLicenseInfo
        {
            LicenseId = new Guid(entry[..16]),
            Expiration = fileTime <= 0 ? null : DateTimeOffset.FromFileTime(fileTime),
            StatusCode = BinaryPrimitives.ReadUInt32LittleEndian(entry[24..]),
            InstanceCount = BinaryPrimitives.ReadUInt32LittleEndian(entry[28..]),
        };
    }

    public void Dispose() => _client.Dispose();

    private IEnumerable<TcLicenseInfo> ReadList(uint indexGroup)
    {
        uint count;
        try
        {
            count = _client.ReadAny<uint>(indexGroup, 1);
        }
#pragma warning disable CA1031 // A licence list the server does not expose reads as empty, not as failure.
        catch (Exception)
#pragma warning restore CA1031
        {
            yield break;
        }

        if (count == 0)
        {
            yield break;
        }

        var raw = _client.ReadAny<byte[]>(indexGroup, 1, [(int)count * EntryBytes]);
        for (var i = 0; i < count; i++)
        {
            var entry = raw.AsSpan(i * EntryBytes, EntryBytes).ToArray();
            yield return Parse(entry) with { Name = TryResolveName(entry.AsSpan(0, 16).ToArray()) };
        }
    }

    private string? TryResolveName(byte[] guid)
    {
        try
        {
            var buffer = new byte[81];
#pragma warning disable CS0618 // ReadWrite(Memory) returns the actual byte count; the result API has no equivalent.
            var length = _client.ReadWrite(LicenseName, 0, buffer.AsMemory(), guid.AsMemory());
#pragma warning restore CS0618
            return length > 1 ? Encoding.ASCII.GetString(buffer, 0, length - 1) : null;
        }
#pragma warning disable CA1031 // Name resolution is cosmetic; the GUID stands in when it fails.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }
}
