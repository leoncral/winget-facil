using System.Collections.ObjectModel;
using Asa.Models;

namespace Asa.Services;

public sealed class PackageStore
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    /// <summary>Saúde do winget, usada para a cor do ponto de status da barra global.</summary>
    public enum WingetHealth
    {
        /// <summary>winget respondeu e a última leitura foi bem-sucedida.</summary>
        Healthy,

        /// <summary>winget está presente, mas a leitura falhou ou ainda não ocorreu.</summary>
        Degraded,

        /// <summary>winget não está disponível.</summary>
        Unhealthy
    }
    private readonly WingetCli _winget;
    private readonly InstalledVersionResolver _versionResolver;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private DateTimeOffset _lastSuccessfulRefresh;

    public PackageStore(WingetCli winget, InstalledVersionResolver? versionResolver = null)
    {
        _winget = winget;
        _versionResolver = versionResolver ?? new InstalledVersionResolver();
    }

    public ObservableCollection<InstalledPackage> Installed { get; } = [];

    public bool HasLoaded { get; private set; }

    public string? ErrorMessage { get; private set; }

    public int UpdateCount => Installed
        .Where(package => package.CanUpdate)
        .Select(package => package.Id)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    /// <summary>Atualizações pendentes no catálogo do winget (mesmo valor de <see cref="UpdateCount"/>).</summary>
    public int PendingCount => UpdateCount;

    public IReadOnlySet<string> InstalledIds => Installed
        .Select(package => package.Id)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Total de itens selecionados nas listas Instalados e Atualizações.</summary>
    public int SelectedCount { get; private set; }

    /// <summary>Saúde do winget, usada para a cor do ponto de status da barra global.</summary>
    public WingetHealth Health { get; private set; } = WingetHealth.Degraded;

    public event EventHandler? Changed;

    /// <summary>Atualiza a contagem de seleção agregada das duas listas de pacotes.</summary>
    public void SetSelectedCount(int value)
    {
        if (SelectedCount == value)
        {
            return;
        }

        SelectedCount = value;
    }

    /// <summary>Atualiza a saúde do winget reportada pela janela principal.</summary>
    public void SetHealth(WingetHealth health)
    {
        if (Health == health)
        {
            return;
        }

        Health = health;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RefreshAsync(bool force = false)
    {
        if (!force &&
            HasLoaded &&
            DateTimeOffset.UtcNow - _lastSuccessfulRefresh < CacheLifetime)
        {
            return;
        }

        await _refreshGate.WaitAsync();
        try
        {
            if (!force &&
                HasLoaded &&
                DateTimeOffset.UtcNow - _lastSuccessfulRefresh < CacheLifetime)
            {
                return;
            }

            try
            {
                var packages = await _winget.GetInstalledPackagesAsync();

                // Pacotes com versão "Unknown" podem ter a versão descoberta localmente
                // (registro de desinstalação ou metadados do executável).
                packages = await _versionResolver.ResolveUnknownVersionsAsync(packages);

                Installed.Clear();
                foreach (var package in packages)
                {
                    Installed.Add(package);
                }

                HasLoaded = true;
                ErrorMessage = null;
                _lastSuccessfulRefresh = DateTimeOffset.UtcNow;
            }
            catch (Exception exception)
            {
                ErrorMessage = exception.Message;
            }
            finally
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }
}
