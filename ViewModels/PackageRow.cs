using Asa.Helpers;
using Asa.Models;

namespace Asa.ViewModels;

public sealed class PackageRow : ObservableObject
{
    private readonly Action<PackageRow> _requestUpdate;
    private bool _isSelected;
    private bool _isQueued;

    public PackageRow(InstalledPackage package, Action<PackageRow> requestUpdate)
    {
        Package = package;
        _requestUpdate = requestUpdate;
        UpdateCommand = new RelayCommand(
            _ => _requestUpdate(this),
            _ => CanUpdate && !IsQueued);
    }

    public InstalledPackage Package { get; private set; }

    /// <summary>
    /// Substitui o pacote subjacente reaproveitando a mesma linha: só dispara as propriedades
    /// que realmente mudaram, preservando seleção/estado de fila e evitando repintar a lista.
    /// </summary>
    public void UpdatePackage(InstalledPackage package)
    {
        if (ReferenceEquals(Package, package))
        {
            return;
        }

        var previous = Package;
        Package = package;

        if (!string.Equals(previous.Name, package.Name, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Initial));
        }

        if (!string.Equals(previous.Id, package.Id, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(Id));
        }

        if (!string.Equals(previous.Version, package.Version, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(CurrentVersion));
        }

        if (!string.Equals(previous.AvailableVersion, package.AvailableVersion, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(AvailableVersion));
        }

        if (!string.Equals(previous.Source, package.Source, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(SourceLabel));
        }

        if (previous.HasUpdate != package.HasUpdate)
        {
            OnPropertyChanged(nameof(HasUpdate));
        }

        if (previous.CanUpdate != package.CanUpdate)
        {
            OnPropertyChanged(nameof(CanUpdate));
            OnPropertyChanged(nameof(ShowInSync));
            UpdateCommand.NotifyCanExecuteChanged();
        }
    }

    public string Name => Package.Name;

    public string Id => Package.Id;

    public string CurrentVersion => Package.CurrentVersionDisplay;

    public string AvailableVersion => Package.AvailableVersionDisplay;

    public string SourceLabel => Package.SourceDisplay;

    public string Initial => Package.Initial;

    public bool HasUpdate => Package.HasUpdate;

    public bool CanUpdate => Package.CanUpdate;

    public bool ShowInSync => !CanUpdate;

    public RelayCommand UpdateCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsQueued
    {
        get => _isQueued;
        private set
        {
            if (SetProperty(ref _isQueued, value))
            {
                UpdateCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(ActionToolTip));
            }
        }
    }

    public string ActionToolTip => IsQueued ? "Na fila" : "Atualizar agora";

    public void MarkQueued()
    {
        IsQueued = true;
        IsSelected = false;
    }
}
