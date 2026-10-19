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

    public InstalledPackage Package { get; }

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
