using System.Text.Json;
using Asa.Helpers;

namespace Asa.Services;

public sealed class SettingsStore : ObservableObject
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;
    private bool _silentInstall = true;

    public SettingsStore()
    {
        _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Asa",
            "settings.json");

        Load();
    }

    public bool SilentInstall
    {
        get => _silentInstall;
        set
        {
            if (SetProperty(ref _silentInstall, value))
            {
                Save();
            }
        }
    }

    public string SettingsPath => _settingsPath;

    private void Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return;
            }

            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<PersistedSettings>(json, JsonOptions);
            if (settings is not null)
            {
                _silentInstall = settings.SilentInstall;
            }
        }
        catch
        {
            // Invalid or unreadable settings fall back to the defaults.
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(
                new PersistedSettings { SilentInstall = SilentInstall },
                JsonOptions);
            File.WriteAllText(_settingsPath, json);
        }
        catch
        {
            // The app remains fully usable if preferences cannot be persisted.
        }
    }

    private sealed class PersistedSettings
    {
        public bool SilentInstall { get; set; } = true;
    }
}
