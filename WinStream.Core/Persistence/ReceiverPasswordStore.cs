using System.Text.Json;
using WinStream.Core.Logging;

namespace WinStream.Core.Persistence;

/// <summary>
/// DPAPI-protected AirPlay Receiver passwords keyed by receiver.
/// </summary>
public sealed class ReceiverPasswordStore : IReceiverPasswordStore
{
    private const int ProtectedEnvelopeVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;
    private readonly object _gate = new();

    public ReceiverPasswordStore(string? settingsDirectory = null)
    {
        var directory = settingsDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinStream");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "receiver-passwords.json");
    }

    public bool TryGet(string receiverKey, out string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiverKey);
        lock (_gate)
        {
            var map = Load();
            if (map.TryGetValue(receiverKey, out var found) && !string.IsNullOrWhiteSpace(found))
            {
                password = found;
                return true;
            }

            password = string.Empty;
            return false;
        }
    }

    public void Save(string receiverKey, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiverKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        lock (_gate)
        {
            var map = Load();
            map[receiverKey] = password;
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
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var raw = File.ReadAllText(_path);
            var json = ProtectedJsonEnvelope.TryUnwrap(raw, ProtectedEnvelopeVersion, out var unwrapped)
                ? unwrapped
                : raw;
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            QuarantineUnlocked(ex);
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void QuarantineUnlocked(Exception ex)
    {
        var backup = _path + ".corrupt";
        try
        {
            if (File.Exists(_path))
            {
                File.Move(_path, backup, overwrite: true);
            }
        }
        catch
        {
            // Best effort — still start empty.
        }

        AppLog.Warn(
            "password",
            $"Receiver password store reset after load failure ({ex.GetType().Name}: {ex.Message}). " +
            $"Backup: {backup}");
    }

    private void Save(Dictionary<string, string> map)
    {
        var json = JsonSerializer.Serialize(map, JsonOptions);
        var payload = ProtectedJsonEnvelope.Wrap(json, ProtectedEnvelopeVersion, "password");
        if (payload is null)
        {
            AppLog.Error(
                "password",
                "Receiver password not saved: Windows could not protect it at rest.");
            return;
        }

        var temp = _path + ".tmp";
        File.WriteAllText(temp, payload);
        File.Move(temp, _path, overwrite: true);
    }
}
