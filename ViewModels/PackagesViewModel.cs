using System.Collections.ObjectModel;
using Asa.Helpers;
using Asa.Models;
using Asa.Services;

namespace Asa.ViewModels;

/// <summary>
/// Estados de apresentação explícitos da lista (F1-5). O estado é derivado dos dados
/// (carregamento/linhas/erro/filtro) e nunca depende de animação para ser compreendido.
/// </summary>
public enum PackagesDisplayState
{
    /// <summary>Primeira carga, ainda sem dados: placeholder neutro (nunca "vazio").</summary>
    Skeleton,

    /// <summary>Recarga com dados já visíveis: lista permanece + anel no cabeçalho.</summary>
    Loading,

    /// <summary>Carregado, com itens visíveis.</summary>
    Loaded,

    /// <summary>Lista real vazia (o winget não devolveu nada / nada pendente).</summary>
    Empty,

    /// <summary>Há itens, mas o filtro de texto não deixou nenhum passar.</summary>
    EmptyFiltered,

    /// <summary>Erro sem dados parciais para exibir (InfoBar assuma o papel).</summary>
    Error
}

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

    /// <summary>
    /// F1-4: o usuário já marcou/desmarcou algo (individual ou select-all/limpar). Depois
    /// de tocada, a seleção é exclusivamente do usuário e NUNCA é refeita automaticamente.
    /// </summary>
    private bool _selectionTouchedByUser;

    /// <summary>
    /// Sobe enquanto o ViewModel altera <see cref="PackageRow.IsSelected"/> por conta própria
    /// (auto-seleção, select-all, limpar, enfileiramento) para que essas mudanças NÃO
    /// contem como "o usuário mexeu na seleção".
    /// </summary>
    private bool _suppressSelectionTouch;

    public PackagesViewModel(PackageStore store, OperationCenter operations, bool updatesOnlyMode)
    {
        _store = store;
        _operations = operations;
        _updatesOnlyMode = updatesOnlyMode;
        _showOnlyUpdates = updatesOnlyMode;

        // Assinatura única por instância (esta classe é criada uma vez no App): as páginas
        // Instalados e Atualizações compartilham o mesmo OperationCenter e cada ViewModel
        // reage às mesmas notificações, sem inscrição duplicada.
        _operations.ItemStateChanged += Operations_ItemStateChanged;
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
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectAllState));
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

    /// <summary>
    /// F1-4: estado do select-all calculado sobre a lista <b>FILTRADA</b> (<see cref="Items"/>):
    /// true = todos os elegíveis marcados; null = parcial (indeterminado); false = nenhum
    /// (ou nenhum elegível visível).
    /// </summary>
    public bool? SelectAllState
    {
        get
        {
            var eligible = Items.Where(row => row.CanUpdate && !row.IsQueued).ToList();
            if (eligible.Count == 0)
            {
                return false;
            }

            var selected = eligible.Count(row => row.IsSelected);
            if (selected == 0)
            {
                return false;
            }

            return selected == eligible.Count ? true : null;
        }
    }

    public bool CanUpdateSelected => SelectedCount > 0;

    /// <summary>Há itens marcados (habilita o "Limpar seleção").</summary>
    public bool HasSelection => SelectedCount > 0;

    public string SelectionButtonText =>
        SelectedCount == 0 ? "Atualizar selecionados" : $"Atualizar selecionados ({SelectedCount})";

    // ---- F1-5: estados de apresentação (derivados; nunca dependem de animação) ----

    /// <summary>Estado explícito atual da página.</summary>
    public PackagesDisplayState DisplayState
    {
        get
        {
            if (IsLoading)
            {
                return _allRows.Count == 0
                    ? PackagesDisplayState.Skeleton
                    : PackagesDisplayState.Loading;
            }

            // Erro sem nada visível: só a InfoBar (a lista NÃO é escondida quando há dados).
            if (HasError && Items.Count == 0)
            {
                return PackagesDisplayState.Error;
            }

            if (!_isLoaded)
            {
                // Ainda não houve nenhuma carga bem-sucedida: nunca mostrar "vazio".
                return PackagesDisplayState.Skeleton;
            }

            if (Items.Count == 0)
            {
                return IsTextFilterActive
                    ? PackagesDisplayState.EmptyFiltered
                    : PackagesDisplayState.Empty;
            }

            return PackagesDisplayState.Loaded;
        }
    }

    /// <summary>Filtro de texto ativo (discrimina vazio real × vazio de filtro).</summary>
    private bool IsTextFilterActive => !string.IsNullOrWhiteSpace(SearchText);

    public bool ShowSkeleton => DisplayState == PackagesDisplayState.Skeleton;

    /// <summary>Recarga com dados visíveis (o anel do cabeçalho já cobre isso).</summary>
    public bool ShowLoading => DisplayState == PackagesDisplayState.Loading;

    public bool ShowList => Items.Count > 0;

    public bool ShowColumnHeader => Items.Count > 0;

    /// <summary>Compat: mesma semântica de <see cref="ShowSkeleton"/> (primeira carga).</summary>
    public bool IsInitialLoading => ShowSkeleton;

    public bool ShowEmpty => DisplayState == PackagesDisplayState.Empty;

    public bool ShowEmptyFiltered => DisplayState == PackagesDisplayState.EmptyFiltered;

    public bool ShowInitialError => DisplayState == PackagesDisplayState.Error;

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

    /// <summary>Mensagem específica para o filtro sem resultado (F1-5).</summary>
    public string EmptyFilteredTitle => $"Nenhum resultado para “{SearchText.Trim()}”.";

    public string EmptyFilteredDescription => "Tente outro nome ou parte do identificador do pacote.";

    public string EmptyFilteredGlyph => "\uE721";

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

    /// <summary>
    /// Select-all sobre a lista FILTRADA. Marca a seleção como tocada pelo usuário:
    /// a partir daí nenhum refresh volta a selecionar nada automaticamente.
    /// </summary>
    public void SetAllVisibleSelected(bool selected)
    {
        _selectionTouchedByUser = true;
        SetRowsSelected(Items.Where(row => row.CanUpdate && !row.IsQueued), selected);
    }

    /// <summary>Limpa toda a seleção (visível ou não) e sinaliza escolha do usuário.</summary>
    public void ClearSelection()
    {
        _selectionTouchedByUser = true;
        SetRowsSelected(_allRows.Where(row => row.IsSelected), selected: false);
    }

    /// <summary>Alterna IsSelected programaticamente sem contar como toque do usuário.</summary>
    private void SetRowsSelected(IEnumerable<PackageRow> rows, bool selected)
    {
        _suppressSelectionTouch = true;
        try
        {
            foreach (var row in rows)
            {
                if (row.IsSelected != selected)
                {
                    row.IsSelected = selected;
                }
            }
        }
        finally
        {
            _suppressSelectionTouch = false;
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
                MarkQueuedProgrammatically(row);
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
            MarkQueuedProgrammatically(row);
            UpdateSelectionState();
        }
    }

    /// <summary>
    /// Enfileira a linha sem que o desmarque interno do <see cref="PackageRow.MarkQueued"/>
    /// conte como toque do usuário na seleção (o enfileiramento não é uma escolha de check).
    /// </summary>
    private void MarkQueuedProgrammatically(PackageRow row)
    {
        _suppressSelectionTouch = true;
        try
        {
            row.MarkQueued();
        }
        finally
        {
            _suppressSelectionTouch = false;
        }
    }

    /// <summary>
    /// Traduz o ciclo de vida de um item da fila (notificado pelo OperationCenter com o Id do
    /// pacote) para o estado da linha correspondente. Busca por Id sem distinção de
    /// maiúsculas/minúsculas; roda na UI thread, então as notificações da linha são seguras.
    /// </summary>
    private void Operations_ItemStateChanged(object? sender, OperationItemEventArgs e)
    {
        var state = e.State switch
        {
            PackageOperationState.Queued => PackageRowState.Queued,
            PackageOperationState.Running => PackageRowState.Running,
            PackageOperationState.Succeeded => PackageRowState.Succeeded,
            PackageOperationState.Failed => PackageRowState.Failed,
            // Apenas a linha que estava em execução exibe "Cancelado"; itens que só estavam
            // na fila (cancelados antes de começar) voltam direto para Idle.
            PackageOperationState.Canceled => e.CanceledBeforeStart
                ? PackageRowState.Idle
                : PackageRowState.Canceled,
            _ => PackageRowState.Idle
        };

        var row = _allRows.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, e.PackageId, StringComparison.OrdinalIgnoreCase));

        row?.ApplyOperationState(state, e.Kind == PackageOperationKind.Upgrade, e.Message);
    }

    private void RebuildFromStore()
    {
        // Guarda a seleção por Id antes de reconstruir: quem continua na lista mantém o
        // check; quem saiu do winget simplesmente sai da seleção (sem exceção).
        var selectedIds = _allRows
            .Where(row => row.IsSelected)
            .Select(row => row.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Reaproveita as linhas já existentes: só cria linha para Id novo e desanexa as que
        // desapareceram, para que seleção/estado sobrevivam e a lista não "pisque".
        var reusableById = new Dictionary<string, PackageRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _allRows)
        {
            if (!reusableById.ContainsKey(row.Id))
            {
                reusableById[row.Id] = row;
            }
        }

        var nextRows = new List<PackageRow>(_store.Installed.Count);
        var keptRows = new HashSet<PackageRow>();
        foreach (var package in _store.Installed)
        {
            PackageRow row;
            if (reusableById.TryGetValue(package.Id, out var reusable) && keptRows.Add(reusable))
            {
                reusable.UpdatePackage(package);
                row = reusable;
            }
            else
            {
                row = new PackageRow(package, RequestUpdate);
                row.PropertyChanged += Row_PropertyChanged;
                keptRows.Add(row);
            }

            nextRows.Add(row);
        }

        foreach (var row in _allRows)
        {
            if (!keptRows.Contains(row))
            {
                row.PropertyChanged -= Row_PropertyChanged;
            }
        }

        _allRows.Clear();
        _allRows.AddRange(nextRows);

        // Se um refresh do store acontecer no meio de uma operação, uma linha nova (ou
        // recriada) para um pacote que continua na fila volta a exibir "Na fila" em vez de
        // ficar em Idle até a conclusão.
        foreach (var row in _allRows)
        {
            if (row.State == PackageRowState.Idle && _operations.Contains(row.Id))
            {
                row.ApplyOperationState(PackageRowState.Queued, isUpgradeOperation: true);
            }
        }

        // Restaura a marca por Id (F1-1). Programático: não conta como toque do usuário.
        _suppressSelectionTouch = true;
        try
        {
            foreach (var row in _allRows)
            {
                var isSelected = selectedIds.Contains(row.Id);
                if (row.IsSelected != isSelected)
                {
                    row.IsSelected = isSelected;
                }
            }
        }
        finally
        {
            _suppressSelectionTouch = false;
        }

        // F1-4: no modo "só atualizações", enquanto o usuário nunca mexeu na seleção,
        // todo item elegível vem marcado (padrão Microsoft Store). Vale no primeiro load
        // e em refreshes; depois de tocada a escolha do usuário (por Id) é preservada e
        // itens novos nascem desmarcados.
        if (_updatesOnlyMode && !_selectionTouchedByUser)
        {
            SetRowsSelected(_allRows.Where(row => row.CanUpdate && !row.IsQueued), selected: true);
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

        var target = filtered.ToList();

        // Diferença mínima em vez de Clear(): as linhas que continuam no resultado ficam nos
        // mesmos containers (sem piscar, com rolagem e seleção preservadas).
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (!target.Contains(Items[i]))
            {
                Items.RemoveAt(i);
            }
        }

        for (var i = 0; i < target.Count; i++)
        {
            var row = target[i];
            var index = Items.IndexOf(row);
            if (index < 0)
            {
                Items.Insert(i, row);
            }
            else if (index != i)
            {
                Items.Move(index, i);
            }
        }

        UpdateSelectionState();
        NotifyStateProperties();
    }

    private void Row_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PackageRow.IsSelected) && !_suppressSelectionTouch)
        {
            // F1-4: marca/desmarca vindo do próprio checkbox da linha = escolha do usuário.
            _selectionTouchedByUser = true;
        }

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
        OnPropertyChanged(nameof(SelectAllState));
    }

    private void NotifyStateProperties()
    {
        OnPropertyChanged(nameof(DisplayState));
        OnPropertyChanged(nameof(ShowSkeleton));
        OnPropertyChanged(nameof(ShowLoading));
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(ShowColumnHeader));
        OnPropertyChanged(nameof(IsInitialLoading));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowEmptyFiltered));
        OnPropertyChanged(nameof(ShowInitialError));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDescription));
        OnPropertyChanged(nameof(EmptyGlyph));
        OnPropertyChanged(nameof(EmptyFilteredTitle));
        OnPropertyChanged(nameof(EmptyFilteredDescription));
        OnPropertyChanged(nameof(EmptyFilteredGlyph));
        OnPropertyChanged(nameof(HeaderSummary));
        OnPropertyChanged(nameof(HasVisibleUpdates));
        OnPropertyChanged(nameof(AllVisibleSelected));
        OnPropertyChanged(nameof(SelectAllState));
        OnPropertyChanged(nameof(HasAnyUpdates));
    }
}
