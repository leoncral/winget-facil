using System.IO;
using System.Text.Json;
using Asa.Models;

namespace Asa.Services;

/// <summary>
/// Serviço de leitura do catálogo curado (F1-7).
/// Carrega <c>Assets/catalog.json</c> uma vez, em memória, e nunca lança exceções:
/// arquivo ausente ou corrompido resulta em um catálogo vazio.
/// </summary>
public sealed class CatalogService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // O app é desempacotado (WindowsPackageType=None), portanto o catálogo não é lido
    // via ms-appx:///. O MSBuild copia Assets/catalog.json para ao lado do executável
    // (<Content CopyToOutputDirectory="PreserveNewest" />), então lemos do disco.
    private static readonly string CatalogPath = Path.Combine(AppContext.BaseDirectory, "Assets", "catalog.json");

    // O catálogo é pequeno e local. Carrega uma vez no pool de threads para não
    // bloquear a UI. As consultas públicas são assíncronas e aguardam essa carga.
    // Nunca lança exceções.
    private readonly Lazy<Task<CatalogData>> _data;

    /// <summary>
    /// Categorias do catálogo. Vazio até que a carga assíncrona seja concluída.
    /// </summary>
    public IReadOnlyList<CatalogCategory> Categories { get; private set; } = new List<CatalogCategory>();

    public CatalogService()
    {
        _data = new Lazy<Task<CatalogData>>(() => Task.Run(LoadAsync));
    }

    /// <summary>
    /// Indica se o pacote é considerado crítico (presente na lista de segurança).
    /// A comparação de IDs ignora maiúsculas/minúsculas.
    /// </summary>
    public Task<bool> IsCriticalAsync(string packageId)
    {
        return IsCriticalAsyncCore(packageId, null, null);
    }

    /// <summary>
    /// Indica se o pacote é considerado crítico: está na lista de segurança ou é um
    /// major upgrade (primeiro segmento da versão disponível maior que o instalado).
    /// </summary>
    public Task<bool> IsCriticalAsync(string packageId, string? installedVersion, string? availableVersion)
    {
        return IsCriticalAsyncCore(packageId, installedVersion, availableVersion);
    }

    /// <summary>
    /// Motivo pelo qual o pacote é crítico, ou vazio quando não é crítico.
    /// </summary>
    public Task<string> CriticalReasonAsync(string packageId)
    {
        return CriticalReasonAsyncCore(packageId, null, null);
    }

    /// <summary>
    /// Motivo pelo qual o pacote é crítico: motivo de segurança ou explicação de
    /// major upgrade. Vazio quando não é crítico.
    /// </summary>
    public Task<string> CriticalReasonAsync(string packageId, string? installedVersion, string? availableVersion)
    {
        return CriticalReasonAsyncCore(packageId, installedVersion, availableVersion);
    }

    /// <summary>
    /// Categorias do catálogo, aguardando a carga se ainda não estiver concluída.
    /// </summary>
    public Task<IReadOnlyList<CatalogCategory>> GetCategoriesAsync()
    {
        return _data.Value.ContinueWith(
            static t => t.IsCompletedSuccessfully
                ? (IReadOnlyList<CatalogCategory>)t.Result.Categories
                : new List<CatalogCategory>());
    }

    private async Task<bool> IsCriticalAsyncCore(string packageId, string? installedVersion, string? availableVersion)
    {
        var data = await _data.Value;
        if (data.Security.Any(e => string.Equals(e.Id, packageId, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        return IsMajorUpgrade(installedVersion, availableVersion);
    }

    private async Task<string> CriticalReasonAsyncCore(string packageId, string? installedVersion, string? availableVersion)
    {
        var data = await _data.Value;
        var entry = data.Security.FirstOrDefault(
            e => string.Equals(e.Id, packageId, StringComparison.OrdinalIgnoreCase));
        if (entry is not null)
        {
            return entry.Reason;
        }
        return IsMajorUpgrade(installedVersion, availableVersion)
            ? $"Atualização major disponível (de {installedVersion} para {availableVersion})."
            : string.Empty;
    }

    private static bool IsMajorUpgrade(string? installedVersion, string? availableVersion)
    {
        var installed = FirstSegment(installedVersion);
        var available = FirstSegment(availableVersion);
        return installed is not null && available is not null && available > installed;
    }

    private static int? FirstSegment(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var text = version.Trim();
        if (text.Length > 1 && (text[0] is 'v' or 'V') && char.IsAsciiDigit(text[1]))
        {
            text = text[1..];
        }

        var dot = text.IndexOf('.');
        var segment = dot >= 0 ? text[..dot] : text;
        return int.TryParse(segment, out var value) ? value : null;
    }

    private async Task<CatalogData> LoadAsync()
    {
        try
        {
            var json = await File.ReadAllTextAsync(CatalogPath);
            var data = JsonSerializer.Deserialize<CatalogData>(json, JsonOptions) ?? new CatalogData();
            data.Categories ??= new List<CatalogCategory>();
            data.Security ??= new List<CatalogSecurityEntry>();
            foreach (var entry in data.Security)
            {
                if (entry.Reason is null)
                {
                    entry.Reason = string.Empty;
                }
            }

            Categories = new List<CatalogCategory>(data.Categories).AsReadOnly();
            return data;
        }
        catch
        {
            // Arquivo ausente ou corrompido: catálogo vazio, sem erro para o usuário.
            Categories = new List<CatalogCategory>().AsReadOnly();
            return new CatalogData();
        }
    }
}
