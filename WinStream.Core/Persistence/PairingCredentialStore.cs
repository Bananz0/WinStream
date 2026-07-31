using System.Text.Json;
using WinStream.Core.Logging;
using WinStream.Core.Protocol.AirPlay2;

namespace WinStream.Core.Persistence;

/// <summary>
/// Persists per-receiver HomeKit pairing credentials so later connects can
/// <c>pair-verify</c> instead of prompting the user again.
/// </summary>
/// <remarks>
/// Entries hold Ed25519 private seeds, so the map is DPAPI-protected for the
/// current user on Windows. A plaintext file from an earlier build is read once
/// and rewritten protected.
/// </remarks>
public sealed class PairingCredentialStore : IPairingCredentialStore
{
    private const int ProtectedEnvelopeVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;
    private readonly object _gate = new();

    public PairingCredentialStore(string? settingsDirectory = null)
    {
        var directory = settingsDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinStream");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "pairings.json");
    }

    public bool TryGet(string receiverKey, out PairingCredentials credentials)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiverKey);
        lock (_gate)
        {
            var map = LoadUnlocked();
            if (map.TryGetValue(receiverKey, out var found) && found.IsComplete)
            {
                credentials = found;
                return true;
            }

            credentials = new PairingCredentials();
            return false;
        }
    }

    public void Save(string receiverKey, PairingCredentials credentials)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiverKey);
        ArgumentNullException.ThrowIfNull(credentials);
        if (!credentials.IsComplete)
        {
            throw new ArgumentException("Incomplete pairing credentials.", nameof(credentials));
        }

        lock (_gate)
        {
            var map = LoadUnlocked();
            map[receiverKey] = credentials;
            SaveUnlocked(map);
        }
    }

    public void Remove(string receiverKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiverKey);
        lock (_gate)
        {
            var map = LoadUnlocked();
            if (map.Remove(receiverKey))
            {
                SaveUnlocked(map);
            }
        }
    }

    private Dictionary<string, PairingCredentials> LoadUnlocked()
    {
        if (!File.Exists(_path))
        {
            return NewMap();
        }

        try
        {
            var raw = File.ReadAllText(_path);
            var json = ProtectedJsonEnvelope.TryUnwrap(raw, ProtectedEnvelopeVersion, out var unwrapped)
                ? unwrapped
                : raw;
            var map = JsonSerializer.Deserialize<Dictionary<string, PairingCredentials>>(json, JsonOptions);
            return map is null ? NewMap() : new Dictionary<string, PairingCredentials>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // Starting empty silently would look like the Mac forgot us; keep the
            // unreadable file so the reset is diagnosable.
            QuarantineUnlocked(ex);
            return NewMap();
        }
    }

    private void SaveUnlocked(Dictionary<string, PairingCredentials> map)
    {
        var json = JsonSerializer.Serialize(map, JsonOptions);
        var payload = ProtectedJsonEnvelope.Wrap(json, ProtectedEnvelopeVersion, "pair");
        if (payload is null)
        {
            // The map holds Ed25519 seeds. Losing the pairing costs one extra Accept
            // prompt; writing the seed unprotected costs the trust it represents.
            AppLog.Error("pair", "Pairing not saved: Windows could not protect it at rest.");
            return;
        }

        // Replace via temp file so a crash mid-write cannot truncate every pairing.
        var temp = _path + ".tmp";
        File.WriteAllText(temp, payload);
        File.Move(temp, _path, overwrite: true);
    }

    private void QuarantineUnlocked(Exception ex)
    {
        var backup = _path + ".corrupt";
        try
        {
            File.Move(_path, backup, overwrite: true);
        }
        catch
        {
            // Best effort — the reset warning below is what the user acts on.
        }

        AppLog.Warn(
            "pair",
            $"Pairings unreadable ({ex.GetType().Name}); reset — the receiver will ask for its " +
            $"AirPlay code again. Previous file kept at {backup}.");
    }

    private static Dictionary<string, PairingCredentials> NewMap() =>
        new(StringComparer.OrdinalIgnoreCase);
}
