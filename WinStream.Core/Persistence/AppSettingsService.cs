namespace WinStream.Core.Persistence;

/// <summary>
/// The single owner of settings.json. Every mutation re-reads the file inside the
/// lock before saving, so a writer holding a stale snapshot cannot erase fields
/// another writer persisted in the meantime.
/// </summary>
public sealed class AppSettingsService
{
    private readonly SettingsStore _store;
    private readonly object _gate = new();
    private AppSettings _settings;

    public AppSettingsService(SettingsStore? store = null)
    {
        _store = store ?? new SettingsStore();
        _settings = _store.Load();
    }

    public AppSettings Settings
    {
        get
        {
            lock (_gate)
            {
                return _settings.Clone();
            }
        }
    }

    public void Update(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate)
        {
            var settings = _store.Load();
            mutate(settings);
            _store.Save(settings);
            _settings = settings;
        }
    }

    /// <summary>
    /// Returns the persisted sender device ID, generating and storing one on first use.
    /// </summary>
    public string EnsureSenderDeviceId()
    {
        lock (_gate)
        {
            var settings = _store.Load();
            if (SenderIdentity.LooksLikeMac(settings.SenderDeviceId))
            {
                _settings = settings;
                return settings.SenderDeviceId!;
            }

            settings.SenderDeviceId = SenderIdentity.CreateLocallyAdministeredMac();
            _store.Save(settings);
            _settings = settings;
            return settings.SenderDeviceId;
        }
    }
}
