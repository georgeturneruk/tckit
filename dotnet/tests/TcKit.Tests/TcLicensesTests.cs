using System.Buffers.Binary;
using TcKit.Ads;

namespace TcKit.Tests;

/// <summary>
/// Licence entry parsing (48-byte wire layout from the licence server, AMS port 30) and the pure
/// half of the stuck-in-Config preflight diagnosis.
/// </summary>
public sealed class TcLicensesTests
{
    private static byte[] Entry(Guid id, DateTimeOffset? expiry, uint status, uint instances = 1)
    {
        var bytes = new byte[48];
        id.TryWriteBytes(bytes.AsSpan(0, 16));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), expiry?.ToFileTime() ?? 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), status);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), instances);
        return bytes;
    }

    private static readonly DateTimeOffset Now = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parse_DecodesTheWireLayout()
    {
        var id = Guid.NewGuid();
        var expiry = new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

        var info = TcLicenses.Parse(Entry(id, expiry, status: 0x254, instances: 3));

        Assert.Equal(id, info.LicenseId);
        Assert.Equal(expiry, info.Expiration);
        Assert.Equal(0x254u, info.StatusCode);
        Assert.Equal(3u, info.InstanceCount);
        Assert.True(info.IsTrial);
        Assert.True(info.IsValid);
    }

    [Fact]
    public void Parse_NoExpiry_IsPermanent()
    {
        var info = TcLicenses.Parse(Entry(Guid.NewGuid(), expiry: null, status: 0));

        Assert.Null(info.Expiration);
        Assert.False(info.IsTrial);
    }

    [Fact]
    public void Diagnose_ExpiredTrial_NamesItAndPointsAtTheDialog()
    {
        var licenses = new[]
        {
            new TcLicenseInfo
            {
                Name = "TC1200 PLC",
                StatusCode = 0x254,
                Expiration = new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero),
            },
        };

        var message = TcLicenses.DiagnoseStuckInConfig(licenses, Now);

        Assert.NotNull(message);
        Assert.Contains("trial licence", message);
        Assert.Contains("TC1200 PLC", message);
        Assert.Contains("2026-06-30", message);
        Assert.Contains("Config", message);
        Assert.Contains("TcXaeShell", message);
    }

    [Fact]
    public void Diagnose_UnnamedLicence_FallsBackToGuid()
    {
        var id = Guid.NewGuid();
        var licenses = new[]
        {
            new TcLicenseInfo { LicenseId = id, StatusCode = 0x254, Expiration = Now.AddDays(-1) },
        };

        var message = TcLicenses.DiagnoseStuckInConfig(licenses, Now);

        Assert.NotNull(message);
        Assert.Contains(id.ToString(), message);
    }

    [Fact]
    public void Diagnose_FutureExpiry_ReturnsNull()
    {
        var licenses = new[]
        {
            new TcLicenseInfo { Name = "TC1200", StatusCode = 0x254, Expiration = Now.AddDays(3) },
        };

        Assert.Null(TcLicenses.DiagnoseStuckInConfig(licenses, Now));
    }

    [Fact]
    public void Diagnose_PermanentAndEmpty_ReturnNull()
    {
        var permanent = new[] { new TcLicenseInfo { Name = "TC1200", StatusCode = 0 } };

        Assert.Null(TcLicenses.DiagnoseStuckInConfig(permanent, Now));
        Assert.Null(TcLicenses.DiagnoseStuckInConfig([], Now));
    }

    [Fact]
    public void Diagnose_EarliestExpiryWins()
    {
        var licenses = new[]
        {
            new TcLicenseInfo { Name = "Later", StatusCode = 0x254, Expiration = Now.AddDays(-1) },
            new TcLicenseInfo { Name = "Earlier", StatusCode = 0x254, Expiration = Now.AddDays(-10) },
        };

        var message = TcLicenses.DiagnoseStuckInConfig(licenses, Now);

        Assert.NotNull(message);
        Assert.Contains("Earlier", message);
    }
}
