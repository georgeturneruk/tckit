using System.Text.Json;
using System.Text.Json.Serialization;
using TcKit.Core.Ports;

namespace TcKit.Core.Security;

/// <summary>
/// <see cref="IPermissionGate"/> backed by a small JSON file at <c>~/.tckit/permissions.json</c>
/// (override the directory with the <c>TCKIT_HOME</c> environment variable). The file is the single
/// source of truth and is hot-reloaded on its mtime, so editing it — or asking the agent to, or a
/// <c>SetPermissions</c> tool call — takes effect on the next tool call with no reconnect.
///
/// Failure stances: a missing file is <see cref="PermissionSettings.Permissive"/> (the stance is
/// opt-in); an unparseable file keeps the last good settings rather than bricking the server; a valid
/// file with an unrecognised <c>mode</c> falls to the safe side (<see cref="PermissionLevel.Read"/>),
/// since a present-but-typo'd mode signals an intent to restrict.
/// </summary>
public sealed class FilePermissionGate : IPermissionGate
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private PermissionSettings _settings = PermissionSettings.Permissive;
    private DateTime? _loadedMtimeUtc;
    private bool _loadedOnce;

    /// <param name="filePath">Explicit file path (for tests). Defaults to the resolved user-global path.</param>
    public FilePermissionGate(string? filePath = null) => _path = filePath ?? DefaultPath();

    /// <summary>The resolved <c>permissions.json</c> path: <c>$TCKIT_HOME/permissions.json</c> or <c>~/.tckit/permissions.json</c>.</summary>
    public static string DefaultPath()
    {
        var home = Environment.GetEnvironmentVariable("TCKIT_HOME");
        var dir = string.IsNullOrWhiteSpace(home)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tckit")
            : home;
        return Path.Combine(dir, "permissions.json");
    }

    public PermissionSettings Current()
    {
        lock (_gate)
        {
            ReloadIfChanged();
            return _settings;
        }
    }

    public string? Deny(PermissionLevel required, string? targetAmsId = null)
    {
        var settings = Current();

        if (settings.Mode < required)
        {
            return $"Permission denied: this operation needs '{Name(required)}' permission but the current "
                + $"mode is '{Name(settings.Mode)}'. Raise it with SetPermissions(mode=\"{Name(required)}\") "
                + $"or edit {_path}.";
        }

        if (required != PermissionLevel.Execute || string.IsNullOrWhiteSpace(targetAmsId))
        {
            return null;
        }

        var target = targetAmsId.Trim();
        if (settings.BlockedNetIds.Any(blocked => Match(blocked, target)))
        {
            return $"Permission denied: target '{target}' is in blocked_net_ids and can never be acted on "
                + $"from this machine. This is a hard guard; edit {_path} to change it.";
        }

        if (settings.AllowedNetIds.Count > 0 && !settings.AllowedNetIds.Any(allowed => Match(allowed, target)))
        {
            return $"Permission denied: target '{target}' is not in allowed_net_ids. Add it with "
                + $"SetPermissions(allowedNetIds=\"...\") or edit {_path}.";
        }

        return null;
    }

    public PermissionSettings Apply(
        PermissionLevel? mode,
        IReadOnlyList<string>? allowedNetIds,
        IReadOnlyList<string>? addBlockedNetIds)
    {
        lock (_gate)
        {
            ReloadIfChanged();

            var blocked = _settings.BlockedNetIds.ToList();
            if (addBlockedNetIds is not null)
            {
                foreach (var candidate in Clean(addBlockedNetIds))
                {
                    if (!blocked.Any(existing => Match(existing, candidate)))
                    {
                        blocked.Add(candidate);
                    }
                }
            }

            var next = _settings with
            {
                Mode = mode ?? _settings.Mode,
                AllowedNetIds = allowedNetIds is null ? _settings.AllowedNetIds : Clean(allowedNetIds),
                BlockedNetIds = blocked,
            };

            Write(next);
            _settings = next;
            return next;
        }
    }

    private void ReloadIfChanged()
    {
        DateTime? mtime = null;
        try
        {
            if (File.Exists(_path))
            {
                mtime = File.GetLastWriteTimeUtc(_path);
            }
        }
        catch (IOException) { /* treat as absent below */ }
        catch (UnauthorizedAccessException) { /* treat as absent below */ }

        if (_loadedOnce && mtime == _loadedMtimeUtc)
        {
            return;
        }

        _loadedOnce = true;
        _loadedMtimeUtc = mtime;

        if (mtime is null)
        {
            _settings = PermissionSettings.Permissive;
            return;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(_path), ReadOptions);
            _settings = FromDto(dto);
        }
        catch (JsonException) { /* keep last good settings */ }
        catch (IOException) { /* keep last good settings */ }
    }

    private void Write(PermissionSettings settings)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var dto = new Dto
        {
            Mode = Name(settings.Mode),
            AllowedNetIds = settings.AllowedNetIds.ToList(),
            BlockedNetIds = settings.BlockedNetIds.ToList(),
        };
        File.WriteAllText(_path, JsonSerializer.Serialize(dto, WriteOptions));

        try
        {
            _loadedMtimeUtc = File.GetLastWriteTimeUtc(_path);
            _loadedOnce = true;
        }
        catch (IOException) { /* mtime refresh is best-effort */ }
    }

    private static PermissionSettings FromDto(Dto? dto)
    {
        if (dto is null)
        {
            return PermissionSettings.Permissive;
        }

        return new PermissionSettings
        {
            Mode = ParseMode(dto.Mode),
            AllowedNetIds = Clean(dto.AllowedNetIds),
            BlockedNetIds = Clean(dto.BlockedNetIds),
        };
    }

    // An absent mode key leaves the stance unrestricted (permissive); a present but unrecognised
    // value falls to the safe side, since editing mode signals an intent to restrict.
    private static PermissionLevel ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" => PermissionLevel.Execute,
        "read" => PermissionLevel.Read,
        "write" => PermissionLevel.Write,
        "execute" => PermissionLevel.Execute,
        _ => PermissionLevel.Read,
    };

    private static IReadOnlyList<string> Clean(IEnumerable<string>? values) => values is null
        ? []
        : values.Select(v => v.Trim()).Where(v => v.Length > 0).ToList();

    private static bool Match(string configured, string target)
        => string.Equals(configured.Trim(), target, StringComparison.OrdinalIgnoreCase);

    private static string Name(PermissionLevel level) => level.ToString().ToLowerInvariant();

    private sealed class Dto
    {
        [JsonPropertyName("mode")] public string? Mode { get; set; }

        [JsonPropertyName("allowed_net_ids")] public List<string>? AllowedNetIds { get; set; }

        [JsonPropertyName("blocked_net_ids")] public List<string>? BlockedNetIds { get; set; }
    }
}
