using System.Collections.ObjectModel;
using Asa.Models;

namespace Asa.Services;

public sealed class PackageStore
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);
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

    public IReadOnlySet<string> InstalledIds => Installed
        .Select(package => package.Id)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? Changed;

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
