using TcKit.Core.Security;

namespace TcKit.Tests;

/// <summary>
/// Tests for the file-backed safety gate: mode tiering, the allow/block NetId semantics, hot-reload on
/// file change, the append-only blocklist, and the failure stances (missing / malformed / bad-mode).
/// Each test writes its own temp file so the cases are isolated and no real ~/.tckit is touched.
/// </summary>
public sealed class FilePermissionGateTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public FilePermissionGateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tckit-perm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "permissions.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException) { /* best-effort cleanup */ }
    }

    private FilePermissionGate Gate() => new(_path);

    private void WriteFile(string json) => File.WriteAllText(_path, json);

    [Fact]
    public void MissingFile_IsPermissive()
    {
        var gate = Gate();

        Assert.Equal(PermissionLevel.Execute, gate.Current().Mode);
        Assert.Null(gate.Deny(PermissionLevel.Execute, "1.2.3.4.1.1"));
        Assert.Null(gate.Deny(PermissionLevel.Write));
    }

    [Fact]
    public void ReadMode_DeniesWriteAndExecute_AllowsRead()
    {
        WriteFile("""{ "mode": "read" }""");
        var gate = Gate();

        Assert.Null(gate.Deny(PermissionLevel.Read));
        Assert.NotNull(gate.Deny(PermissionLevel.Write));
        Assert.NotNull(gate.Deny(PermissionLevel.Execute, "1.2.3.4.1.1"));
    }

    [Fact]
    public void WriteMode_AllowsWrite_DeniesExecute()
    {
        WriteFile("""{ "mode": "write" }""");
        var gate = Gate();

        Assert.Null(gate.Deny(PermissionLevel.Read));
        Assert.Null(gate.Deny(PermissionLevel.Write));
        Assert.NotNull(gate.Deny(PermissionLevel.Execute, "1.2.3.4.1.1"));
    }

    [Fact]
    public void BlockedNetId_IsDeniedEvenInExecuteMode()
    {
        WriteFile("""{ "mode": "execute", "blocked_net_ids": ["10.0.0.5.1.1"] }""");
        var gate = Gate();

        Assert.NotNull(gate.Deny(PermissionLevel.Execute, "10.0.0.5.1.1"));
        Assert.Null(gate.Deny(PermissionLevel.Execute, "10.0.0.6.1.1"));
    }

    [Fact]
    public void Allowlist_PermitsOnlyListedTargets()
    {
        WriteFile("""{ "mode": "execute", "allowed_net_ids": ["1.2.3.4.1.1"] }""");
        var gate = Gate();

        Assert.Null(gate.Deny(PermissionLevel.Execute, "1.2.3.4.1.1"));
        Assert.NotNull(gate.Deny(PermissionLevel.Execute, "9.9.9.9.1.1"));
    }

    [Fact]
    public void EmptyAllowlist_PermitsAnyNonBlockedTarget()
    {
        WriteFile("""{ "mode": "execute" }""");
        var gate = Gate();

        Assert.Null(gate.Deny(PermissionLevel.Execute, "anything.1.1"));
    }

    [Fact]
    public void Block_WinsOverAllow()
    {
        WriteFile("""
            { "mode": "execute", "allowed_net_ids": ["1.2.3.4.1.1"], "blocked_net_ids": ["1.2.3.4.1.1"] }
            """);
        var gate = Gate();

        Assert.NotNull(gate.Deny(PermissionLevel.Execute, "1.2.3.4.1.1"));
    }

    [Fact]
    public void NetIdMatch_IsCaseInsensitiveAndTrimmed()
    {
        WriteFile("""{ "mode": "execute", "blocked_net_ids": ["  ABC.1.1  "] }""");
        var gate = Gate();

        Assert.NotNull(gate.Deny(PermissionLevel.Execute, "abc.1.1"));
    }

    [Fact]
    public void ExecuteGate_WithoutTarget_ChecksModeOnly()
    {
        // A blocklist is irrelevant when the execute call carries no target to check.
        WriteFile("""{ "mode": "execute", "blocked_net_ids": ["10.0.0.5.1.1"] }""");
        var gate = Gate();

        Assert.Null(gate.Deny(PermissionLevel.Execute));
    }

    [Fact]
    public void HotReload_PicksUpFileChange()
    {
        WriteFile("""{ "mode": "read" }""");
        var gate = Gate();
        Assert.NotNull(gate.Deny(PermissionLevel.Write));

        // Rewrite with a later timestamp so the mtime check trips regardless of clock granularity.
        WriteFile("""{ "mode": "execute" }""");
        File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddSeconds(5));

        Assert.Null(gate.Deny(PermissionLevel.Write));
        Assert.Null(gate.Deny(PermissionLevel.Execute, "1.2.3.4.1.1"));
    }

    [Fact]
    public void UnknownMode_FallsToRead()
    {
        WriteFile("""{ "mode": "raed" }""");
        var gate = Gate();

        Assert.Equal(PermissionLevel.Read, gate.Current().Mode);
        Assert.NotNull(gate.Deny(PermissionLevel.Write));
    }

    [Fact]
    public void MalformedJson_KeepsLastGoodSettings()
    {
        WriteFile("""{ "mode": "read" }""");
        var gate = Gate();
        Assert.Equal(PermissionLevel.Read, gate.Current().Mode);

        WriteFile("{ this is not json");
        File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddSeconds(5));

        // The broken file must not brick the gate or silently widen access.
        Assert.Equal(PermissionLevel.Read, gate.Current().Mode);
    }

    [Fact]
    public void Apply_SetsModeAndAllowlist_AndPersists()
    {
        var gate = Gate();

        var next = gate.Apply(PermissionLevel.Write, ["1.2.3.4.1.1"], null);

        Assert.Equal(PermissionLevel.Write, next.Mode);
        Assert.Equal(["1.2.3.4.1.1"], next.AllowedNetIds);
        // A fresh gate reads the same stance back from disk.
        Assert.Equal(PermissionLevel.Write, new FilePermissionGate(_path).Current().Mode);
    }

    [Fact]
    public void Apply_NullFacets_LeaveThemUnchanged()
    {
        WriteFile("""{ "mode": "write", "allowed_net_ids": ["1.2.3.4.1.1"] }""");
        var gate = Gate();

        var next = gate.Apply(mode: null, allowedNetIds: null, addBlockedNetIds: null);

        Assert.Equal(PermissionLevel.Write, next.Mode);
        Assert.Equal(["1.2.3.4.1.1"], next.AllowedNetIds);
    }

    [Fact]
    public void Apply_EmptyAllowlist_ClearsIt()
    {
        WriteFile("""{ "mode": "execute", "allowed_net_ids": ["1.2.3.4.1.1"] }""");
        var gate = Gate();

        var next = gate.Apply(mode: null, allowedNetIds: [], addBlockedNetIds: null);

        Assert.Empty(next.AllowedNetIds);
    }

    [Fact]
    public void Apply_AppendsBlocked_ButNeverRemoves()
    {
        WriteFile("""{ "mode": "execute", "blocked_net_ids": ["10.0.0.5.1.1"] }""");
        var gate = Gate();

        var next = gate.Apply(mode: null, allowedNetIds: null, addBlockedNetIds: ["10.0.0.6.1.1"]);

        Assert.Contains("10.0.0.5.1.1", next.BlockedNetIds); // pre-existing block survives
        Assert.Contains("10.0.0.6.1.1", next.BlockedNetIds); // new block appended
    }

    [Fact]
    public void Apply_AppendBlocked_IsIdempotent()
    {
        WriteFile("""{ "mode": "execute", "blocked_net_ids": ["10.0.0.5.1.1"] }""");
        var gate = Gate();

        var next = gate.Apply(mode: null, allowedNetIds: null, addBlockedNetIds: ["10.0.0.5.1.1"]);

        Assert.Single(next.BlockedNetIds);
    }
}
