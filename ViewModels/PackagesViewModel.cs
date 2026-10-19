using System.Collections.ObjectModel;
using Asa.Helpers;
using Asa.Models;
using Asa.Services;

namespace Asa.ViewModels;

public sealed class PackagesViewModel : ObservableObject
{
    private readonly PackageStore _store;
    private readonly OperationCenter _operations;
    private readonly bool _updatesOnlyMode;
    private readonly List<PackageRow> _allRows = [];
    private string _searchText = string.Empty;
    private bool _showOnlyUpdates;
    private bool _isLoading;
    private bool _isLoaded;
    private string? _errorMessage;
    private int _selectedCount;
    private int _updateCount;
    private int _installedCount;

    public PackagesViewModel(PackageStore store, OperationCenter operations, bool updatesOnlyMode)
    {
        _store = store;
        _operations = operations;
        _updatesOnlyMode = updatesOnlyMode;
        _showOnlyUpdates = updatesOnlyMode;
    }

    public ObservableCollection<PackageRow> Items { get; } = [];

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFilter();
            }
        }
    }

    public bool ShowOnlyUpdates
    {
        get => _showOnlyUpdates;
        set
        {
            if (SetProperty(ref _showOnlyUpdates, value))
            {
                ApplyFilter();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                NotifyStateProperties();
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
                NotifyStateProperties();
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public int SelectedCount
    {
        get => _selectedCount;
        private set
        {
            if (SetProperty(ref _selectedCount, value))
            {
                OnPropertyChanged(nameof(SelectionButtonText));
                OnPropertyChanged(nameof(CanUpdateSelected));
            }
        }
    }

    public int UpdateCount
    {
        get => _updateCount;
        private set
        {
            if (SetProperty(ref _updateCount, value))
            {
                OnPropertyChanged(nameof(HeaderSummary));
                OnPropertyChanged(nameof(HasAnyUpdates));
            }
        }
    }

    public int InstalledCount
    {
        get => _installedCount;
        private set
        {
            if (SetProperty(ref _installedCount, value))
            {
                OnPropertyChanged(nameof(HeaderSummary));
            }
        }
    }

    public bool HasAnyUpdates => UpdateCount > 0;

    public bool HasVisibleUpdates => Items.Any(row => row.CanUpdate && !row.IsQueued);

    public bool AllVisibleSelected =>
        HasVisibleUpdates && Items.Where(row => row.CanUpdate && !row.IsQueued).All(row => row.IsSelected);

    public bool CanUpdateSelected => SelectedCount > 0;

    public string SelectionButtonText =>
        SelectedCount == 0 ? "Atualizar selecionados" : $"Atualizar selecionados ({SelectedCount})";

    public bool ShowList => Items.Count > 0;

    public bool ShowColumnHeader => Items.Count > 0;

    public bool IsInitialLoading => IsLoading && Items.Count == 0;

    public bool ShowEmpty => _isLoaded && !IsLoading && !HasError && Items.Count == 0;

    public string EmptyTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                return "Nada encontrado";
            }

            if (ShowOnlyUpdates || _updatesOnlyMode)
            {
                return HasAnyUpdates ? "Nenhuma atualização encontrada" : "Tudo em dia";
            }

            return "Nenhum programa por aqui";
        }
    }

    public string EmptyDescription
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                return "Tente outro nome ou parte do identificador do pacote.";
            }

            if (ShowOnlyUpdates || _updatesOnlyMode)
            {
                return HasAnyUpdates
                    ? "Ajuste a busca para encontrar a atualização que procura."
                    : "Seus programas reconhecidos pelo winget já estão atualizados.";
            }

            return "O winget não encontrou programas instalados que possam ser listados.";
        }
    }

    public string EmptyGlyph
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                return "\uE721";
            }

            return ShowOnlyUpdates || _updatesOnlyMode ? "\uE73E" : "\uE71D";
        }
    }

    public string HeaderSummary
    {
        get
        {
            if (!_isLoaded && IsLoading)
            {
                return "Lendo os programas instalados…";
            }

            if (!_isLoaded && HasError)
            {
                return "Não foi possível ler seus programas.";
            }

            if (_updatesOnlyMode)
            {
                return UpdateCount switch
                {
                    0 => "Nenhuma atualização pendente.",
                    1 => "1 atualização disponível.",
                    _ => $"{UpdateCount} atualizações disponíveis."
                };
            }

            if (InstalledCount == 0)
            {
                return "Nenhum programa foi reconhecido pelo winget.";
            }

            return UpdateCount == 0
                ? $"{InstalledCount} programas instalados · tudo em dia."
                : $"{InstalledCount} programas instalados · {UpdateCount} com atualização.";
        }
    }

    public async Task LoadAsync(bool force = false)
    {
        IsLoading = true;

        try
        {
            await _store.RefreshAsync(force);
            RebuildFromStore();
        }
        finally
        {
            IsLoading = false;
            _isLoaded = _store.HasLoaded;
            NotifyStateProperties();
        }
    }

    public void RefreshFromStore()
    {
        RebuildFromStore();
        _isLoaded = _store.HasLoaded;
        NotifyStateProperties();
    }

    public void SetAllVisibleSelected(bool selected)
    {
        foreach (var row in Items.Where(row => row.CanUpdate && !row.IsQueued))
        {
            row.IsSelected = selected;
        }
    }

    public int QueueSelectedUpdates()
    {
        var rows = Items
            .Where(row => row.IsSelected && row.CanUpdate && !row.IsQueued)
            .ToList();

        var count = 0;
        foreach (var row in rows)
        {
            if (_operations.EnqueueUpgrade(row.Package))
            {
                row.MarkQueued();
                count++;
            }
        }

        UpdateSelectionState();
        return count;
    }

    private void RequestUpdate(PackageRow row)
    {
        if (!row.CanUpdate || row.IsQueued)
        {
            return;
        }

        if (_operations.EnqueueUpgrade(row.Package))
        {
            row.MarkQueued();
            UpdateSelectionState();
        }
    }

    private void RebuildFromStore()
    {
        foreach (var row in _allRows)
        {
            row.PropertyChanged -= Row_PropertyChanged;
        }

        _allRows.Clear();
        foreach (var package in _store.Installed)
        {
            var row = new PackageRow(package, RequestUpdate);
            row.PropertyChanged += Row_PropertyChanged;
            _allRows.Add(row);
        }

        ErrorMessage = _store.ErrorMessage;
        InstalledCount = _allRows.Count;
        UpdateCount = _allRows
            .Where(row => row.CanUpdate)
            .Select(row => row.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filtered = _allRows.AsEnumerable();

        if (_showOnlyUpdates)
        {
            filtered = filtered.Where(row => row.CanUpdate);
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var query = SearchText.Trim();
            filtered = filtered.Where(row =>
                row.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                row.Id.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        Items.Clear();
        foreach (var row in filtered)
        {
            Items.Add(row);
        }

        UpdateSelectionState();
        NotifyStateProperties();
    }

    private void Row_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PackageRow.IsSelected) or nameof(PackageRow.IsQueued))
        {
            UpdateSelectionState();
        }
    }

    private void UpdateSelectionState()
    {
        SelectedCount = Items.Count(row => row.IsSelected && row.CanUpdate && !row.IsQueued);
        OnPropertyChanged(nameof(HasVisibleUpdates));
        OnPropertyChanged(nameof(AllVisibleSelected));
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(ShowColumnHeader));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    private void NotifyStateProperties()
    {
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(ShowColumnHeader));
        OnPropertyChanged(nameof(IsInitialLoading));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDescription));
        OnPropertyChanged(nameof(EmptyGlyph));
        OnPropertyChanged(nameof(HeaderSummary));
        OnPropertyChanged(nameof(HasVisibleUpdates));
        OnPropertyChanged(nameof(AllVisibleSelected));
        OnPropertyChanged(nameof(HasAnyUpdates));
    }
}
