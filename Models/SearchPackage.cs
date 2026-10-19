using Asa.Helpers;

namespace Asa.Models;

public sealed class SearchPackage : ObservableObject
{
    private bool _isInstalled;
    private bool _isQueued;

    public SearchPackage(string name, string id, string version, string source)
    {
        Name = string.IsNullOrWhiteSpace(name) ? id : name;
        Id = id;
        Version = string.IsNullOrWhiteSpace(version) ? "Desconhecida" : version;
        Source = string.IsNullOrWhiteSpace(source) ? "winget" : source;
    }

    public string Name { get; }

    public string Id { get; }

    public string Version { get; }

    public string Source { get; }

    public string Initial => WingetDisplay.Initial(Name);

    public string VersionDisplay => WingetDisplay.Version(Version);

    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (SetProperty(ref _isInstalled, value))
            {
                OnPropertyChanged(nameof(CanInstall));
                OnPropertyChanged(nameof(ActionText));
            }
        }
    }

    public bool IsQueued
    {
        get => _isQueued;
        set
        {
            if (SetProperty(ref _isQueued, value))
            {
                OnPropertyChanged(nameof(CanInstall));
                OnPropertyChanged(nameof(ActionText));
            }
        }
    }

    public bool CanInstall => !IsInstalled && !IsQueued;

    public string ActionText => IsInstalled ? "Instalado" : IsQueued ? "Na fila" : "Instalar";
}
