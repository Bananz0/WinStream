using System.Text.Json;
using WinStream.Core.Logging;

namespace WinStream.Core.Persistence;

/// <summary>
/// LAN PIN credentials for WinStream Link companions — never Apple HKP.
/// </summary>
/// <remarks>
/// A PIN grants audio access to a companion on the LAN, so the map is DPAPI-protected
/// for the current user just like <see cref="PairingCredentialStore"/>.
/// </remarks>
public sealed class LinkCredentialStore : ILinkCredentialStore
{
    private const int ProtectedEnvelopeVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;
    private readonly object _gate = new();

    public LinkCredentialStore(string? settingsDirectory = null)
    {
        var directory = settingsDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinStream");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "link-credentials.json");
    }

    public bool TryGetPin(string receiverKey, out string pin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiverKey);
        lock (_gate)
        {
            var map = Load();
            if (map.TryGetValue(receiverKey, out var found) && !string.IsNullOrWhiteSpace(found))
            {
                pin = found;
                return true;
            }

            pin = string.Empty;
            return false;
        }
    }

    public void SavePin(string receiverKey, string pin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiverKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(pin);
        lock (_gate)
        {
            var map = Load();
            map[receiverKey] = pin;
            Save(map);
        }
    }

    public void Remove(string receiverKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiverKey);
        lock (_gate)
        {
            var map = Load();
            if (map.Remove(receiverKey))
            {
                Save(map);
            }
        }
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var raw = File.ReadAllText(_path);
            var json = ProtectedJsonEnvelope.TryUnwrap(raw, ProtectedEnvelopeVersion, out var unwrapped)
                ? unwrapped
                : raw;
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            AppLog.Info("link", $"Link credential load failed: {ex.Message}");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void Save(Dictionary<string, string> map)
    {
        var json = JsonSerializer.Serialize(map, JsonOptions);
        var payload = ProtectedJsonEnvelope.Wrap(json, ProtectedEnvelopeVersion, "link");
        if (payload is null)
        {
            AppLog.Error("link", "Link PIN not saved: Windows could not protect it at rest.");
            return;
        }

        // Replace via temp file so a crash mid-write cannot truncate every saved PIN.
        var temp = _path + ".tmp";
        File.WriteAllText(temp, payload);
        File.Move(temp, _path, overwrite: true);
    }
}
