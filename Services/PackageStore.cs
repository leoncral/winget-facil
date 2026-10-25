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
        if (!TrySetHealth(health))
        {
            return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Atualiza <see cref="Health"/> sem notificar <see cref="Changed"/>.
    /// Usado por <see cref="RefreshAsync"/>, que já notifica uma única vez no <c>finally</c>.
    /// Retorna <c>true</c> quando o valor mudou.
    /// </summary>
    private bool TrySetHealth(WingetHealth health)
    {
        if (Health == health)
        {
            return false;
        }

        Health = health;
        return true;
    }

    /// <summary>
    /// Atualiza o catálogo de pacotes instalados.
    /// Retorna <c>true</c> quando o catálogo em memória reflete uma leitura bem-sucedida
    /// (inclusive um cache ainda válido); <c>false</c> quando a leitura falhou, caso em que
    /// o snapshot anterior é mantido e <see cref="ErrorMessage"/> explica a falha.
    /// Chamadores que dependem dos dados para tomar decisões (em vez de só exibi-los)
    /// precisam verificar o retorno: o snapshot retido pode estar desatualizado.
    /// <see cref="Health"/> acompanha as leituras reais: sucesso vira <c>Healthy</c> e
    /// falha vira <c>Degraded</c>. Um cache válido não é uma nova leitura e por isso não
    /// altera a saúde — ela continua refletindo a última tentativa de leitura.
    /// </summary>
    public async Task<bool> RefreshAsync(bool force = false)
    {
        if (!force &&
            HasLoaded &&
            DateTimeOffset.UtcNow - _lastSuccessfulRefresh < CacheLifetime)
        {
            return true;
        }

        await _refreshGate.WaitAsync();
        try
        {
            if (!force &&
                HasLoaded &&
                DateTimeOffset.UtcNow - _lastSuccessfulRefresh < CacheLifetime)
            {
                return true;
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

                // Uma leitura bem-sucedida comprova que o winget respondeu: mesmo que o
                // estado anterior fosse Unhealthy (winget ausente na inicialização),
                // o ponto de status deve voltar a Healthy. O Changed sai no finally.
                TrySetHealth(WingetHealth.Healthy);
                return true;
            }
            catch (Exception exception)
            {
                ErrorMessage = exception.Message;

                // A falha de leitura mantém o snapshot anterior, mas o estado precisa
                // refletir que a última tentativa não foi concluída. Preserva Unhealthy:
                // essa condição vem da verificação de versão, que é mais forte do que uma
                // falha de leitura e não deve ser mascarada por ela.
                if (Health != WingetHealth.Unhealthy)
                {
                    TrySetHealth(WingetHealth.Degraded);
                }

                return false;
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
