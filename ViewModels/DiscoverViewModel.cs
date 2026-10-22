using System.Collections.ObjectModel;
using Asa.Helpers;
using Asa.Models;
using Asa.Services;

namespace Asa.ViewModels;

public sealed class DiscoverViewModel : ObservableObject
{
    private readonly WingetCli _winget;
    private readonly PackageStore _store;
    private readonly OperationCenter _operations;
    private int _searchVersion;
    private string _query = string.Empty;
    private bool _isSearching;
    private bool _hasSearched;
    private bool _isLoadingCategories;
    private string? _errorMessage;

    private readonly CatalogService _catalog = App.Catalog;

    public DiscoverViewModel(WingetCli winget, PackageStore store, OperationCenter operations)
    {
        _winget = winget;
        _store = store;
        _operations = operations;
        // Carrega as categorias do catálogo curado sem bloquear a construção da página.
        _ = LoadCategoriesAsync();
    }

    public ObservableCollection<SearchPackage> Results { get; } = [];

    public IReadOnlyList<CatalogCategory> Categories { get; private set; } = new List<CatalogCategory>();

    public bool HasCategories => Categories.Count > 0;

    public bool IsLoadingCategories
    {
        get => _isLoadingCategories;
        private set
        {
            if (SetProperty(ref _isLoadingCategories, value))
            {
                OnPropertyChanged(nameof(IsEmptyCategories));
            }
        }
    }

    // Só exibe o estado vazio do catálogo quando as categorias não carregaram
    // e não estão mais em carga — evita mensagem transitória enquanto assíncrono roda.
    public bool IsEmptyCategories => !HasCategories && !IsLoadingCategories;

    public IReadOnlyList<string> Suggestions { get; } =
    [
        "Visual Studio Code",
        "Firefox",
        "7-Zip",
        "Spotify",
        "PowerToys",
        "Discord"
    ];

    public string Query
    {
        get => _query;
        private set
        {
            if (SetProperty(ref _query, value))
            {
                OnPropertyChanged(nameof(ResultsSummary));
            }
        }
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (SetProperty(ref _isSearching, value))
            {
                NotifyStateProperties();
            }
        }
    }

    public bool HasSearched
    {
        get => _hasSearched;
        private set
        {
            if (SetProperty(ref _hasSearched, value))
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

    public bool ShowWelcome => !HasSearched && !IsSearching && !HasError;

    public bool ShowLoading => IsSearching;

    public bool ShowResults => HasSearched && !IsSearching && !HasError && Results.Count > 0;

    public bool ShowNoResults => HasSearched && !IsSearching && !HasError && Results.Count == 0;

    public bool ShowError => HasError && !IsSearching;

    public string ResultsSummary => Results.Count switch
    {
        0 => $"Nenhum resultado para “{Query}”",
        1 => $"1 resultado para “{Query}”",
        _ => $"{Results.Count} resultados para “{Query}”"
    };

    public void Reset()
    {
        _searchVersion++;
        Query = string.Empty;
        Results.Clear();
        ErrorMessage = null;
        HasSearched = false;
        IsSearching = false;
        NotifyStateProperties();
    }

    public async Task LoadCategoriesAsync()
    {
        IsLoadingCategories = true;
        try
        {
            Categories = await _catalog.GetCategoriesAsync();
        }
        catch
        {
            // Falha ao carregar o catálogo curado: mantém a lista vazia e exibe o estado vazio.
            // Nunca deixa exceção escapar da carga de categorias.
            Categories = new List<CatalogCategory>();
        }
        finally
        {
            IsLoadingCategories = false;
        }

        OnPropertyChanged(nameof(Categories));
        OnPropertyChanged(nameof(HasCategories));
        OnPropertyChanged(nameof(IsEmptyCategories));
    }

    public async Task SearchAsync(string query, CancellationToken cancellationToken)
    {
        var version = ++_searchVersion;
        var trimmed = query.Trim();

        if (trimmed.Length == 0)
        {
            Reset();
            return;
        }

        Query = trimmed;
        IsSearching = true;
        HasSearched = false;
        ErrorMessage = null;
        Results.Clear();
        NotifyStateProperties();

        try
        {
            var results = await _winget.SearchPackagesAsync(trimmed, cancellationToken);

            if (version != _searchVersion || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var installedIds = _store.InstalledIds;
            foreach (var package in results)
            {
                package.IsInstalled = installedIds.Contains(package.Id);
                package.IsQueued = _operations.Contains(package.Id);
                Results.Add(package);
            }

            HasSearched = true;
            NotifyStateProperties();
        }
        catch (OperationCanceledException)
        {
            // A newer search replaced this one.
        }
        catch (Exception exception)
        {
            if (version != _searchVersion || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            ErrorMessage = exception.Message;
            HasSearched = true;
        }
        finally
        {
            if (version == _searchVersion)
            {
                IsSearching = false;
                NotifyStateProperties();
            }
        }
    }

    public void RefreshInstalledState()
    {
        var installedIds = _store.InstalledIds;
        foreach (var result in Results)
        {
            result.IsInstalled = installedIds.Contains(result.Id);
            result.IsQueued = _operations.Contains(result.Id);
        }
    }

    private void NotifyStateProperties()
    {
        OnPropertyChanged(nameof(ShowWelcome));
        OnPropertyChanged(nameof(ShowLoading));
        OnPropertyChanged(nameof(ShowResults));
        OnPropertyChanged(nameof(ShowNoResults));
        OnPropertyChanged(nameof(ShowError));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ResultsSummary));
    }
}
