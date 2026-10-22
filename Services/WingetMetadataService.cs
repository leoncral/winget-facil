using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Asa.Models;

namespace Asa.Services;

/// <summary>
/// Metadados sob demanda de um pacote (tamanho do download) obtidos via
/// <c>winget show --id &lt;id&gt; --exact</c>, com cache em disco (TTL 24 h).
/// Nunca lança: arquivo ausente/corrompido ou falha no winget resulta em null.
/// </summary>
public sealed partial class WingetMetadataService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly WingetCli _winget;
    private readonly string _cachePath;
    private readonly object _cacheLock = new();

    public WingetMetadataService() : this(App.Winget)
    {
    }

    internal WingetMetadataService(WingetCli winget)
    {
        _winget = winget;
        _cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Asa",
            "metadata-cache.json");
    }

    /// <summary>
    /// Obtém os metadados do pacote, usando o cache em disco quando ainda estiver
    /// válido (TTL 24 h). Retorna null quando o winget não estiver disponível, a
    /// consulta falhar ou não houver dado útil (sem placeholder falso).
    /// </summary>
    public async Task<WingetMetadata?> GetMetadataAsync(
        string packageId,
        string? availableVersion,
        string? source = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return null;
        }

        var normalizedId = packageId.Trim();
        var normalizedSource = string.IsNullOrWhiteSpace(source) ? null : source.Trim();

        // Tenta o cache em disco primeiro. A chave é composta por Id + Versão + Origem, e a
        // versão armazenada é validada contra a solicitada: se não bater, busca novamente.
        var cached = GetFromCache(normalizedId, availableVersion, normalizedSource);
        if (cached is not null && !IsExpired(cached.RetrievedAt))
        {
            return cached;
        }

        // winget não disponível: sem metadados (null, nunca placeholder).
        if (!_winget.IsAvailable)
        {
            return null;
        }

        WingetCommandResult result;
        try
        {
            result = await _winget.ShowAsync(normalizedId, normalizedSource, availableVersion, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        if (!result.IsSuccess)
        {
            return null;
        }

        var metadata = ParseMetadata(normalizedId, result.Output);
        if (metadata is not null)
        {
            SaveToCache(metadata, normalizedSource);
        }

        return metadata;
    }

    private WingetMetadata? GetFromCache(string packageId, string? availableVersion, string? source)
    {
        lock (_cacheLock)
        {
            try
            {
                if (!File.Exists(_cachePath))
                {
                    return null;
                }

                var json = File.ReadAllText(_cachePath);
                var file = JsonSerializer.Deserialize<CacheFile>(json, JsonOptions) ?? new CacheFile();

                // Remove entradas expiradas para não deixar o arquivo crescer indefinidamente.
                var valid = file.Entries.Where(e => !IsExpired(e.RetrievedAt)).ToList();
                if (valid.Count != file.Entries.Count)
                {
                    file.Entries = valid;
                    Persist(file);
                }

                var entry = file.Entries.FirstOrDefault(e =>
                    string.Equals(e.Id, packageId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Source ?? string.Empty, source ?? string.Empty, StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                {
                    return null;
                }

                // Valida a versão armazenada contra a solicitada: versão diferente (ou ausente)
                // significa cache desatualizado para essa versão/origem — buscar novamente.
                if (!string.IsNullOrEmpty(availableVersion) &&
                    !string.Equals(entry.Version, availableVersion, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return ToModel(entry);
            }
            catch
            {
                // Arquivo ausente ou corrompido: sem cache.
                return null;
            }
        }
    }

    private void SaveToCache(WingetMetadata metadata, string? source)
    {
        lock (_cacheLock)
        {
            try
            {
                var file = new CacheFile();
                try
                {
                    if (File.Exists(_cachePath))
                    {
                        var json = File.ReadAllText(_cachePath);
                        file = JsonSerializer.Deserialize<CacheFile>(json, JsonOptions) ?? new CacheFile();
                    }
                }
                catch
                {
                    file = new CacheFile();
                }

                // Remove versão antiga do mesmo ID e origem (se houver) e entradas expiradas.
                file.Entries.RemoveAll(
                    e => (string.Equals(e.Id, metadata.Id, StringComparison.OrdinalIgnoreCase) &&
                          string.Equals(e.Source ?? string.Empty, source ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                          || IsExpired(e.RetrievedAt));
                file.Entries.Add(ToCacheEntry(metadata, source));

                Persist(file);
            }
            catch
            {
                // Persistência do cache nunca deve impedir o funcionamento do app.
            }
        }
    }

    private void Persist(CacheFile file)
    {
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_cachePath, JsonSerializer.Serialize(file, JsonOptions));
        }
        catch
        {
            // Ignorar falhas de gravação de cache.
        }
    }

    private static bool IsExpired(DateTimeOffset retrievedAt) =>
        DateTimeOffset.UtcNow - retrievedAt > CacheTtl;

    private static WingetMetadata? ParseMetadata(string packageId, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        string? name = null;
        string? version = null;
        string? publisher = null;
        long? downloadSizeBytes = null;
        string? downloadSizeDisplay = null;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = AnsiEscapeRegex().Replace(rawLine, string.Empty)
                .Replace("\r", string.Empty, StringComparison.Ordinal)
                .Trim();

            if (line.Length == 0)
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (string.Equals(key, "Nome", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "Name", StringComparison.OrdinalIgnoreCase))
            {
                name = value.Length == 0 ? null : value;
            }
            else if (string.Equals(key, "Versão", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(key, "Version", StringComparison.OrdinalIgnoreCase))
            {
                version = value.Length == 0 ? null : value;
            }
            else if (string.Equals(key, "Publicador", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(key, "Publisher", StringComparison.OrdinalIgnoreCase))
            {
                publisher = value.Length == 0 ? null : value;
            }
            else if (string.Equals(key, "Tamanho", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(key, "Size", StringComparison.OrdinalIgnoreCase))
            {
                var size = ParseSize(value);
                if (size is not null)
                {
                    downloadSizeBytes = size.Value.bytes;
                    downloadSizeDisplay = size.Value.display;
                }
            }
        }

        // Só retorna metadados quando há algo além do ID (evita entradas vazias no cache).
        if (name is null && version is null && publisher is null && downloadSizeBytes is null)
        {
            return null;
        }

        return new WingetMetadata
        {
            Id = packageId,
            Name = name,
            Version = version,
            Publisher = publisher,
            DownloadSizeBytes = downloadSizeBytes,
            DownloadSizeDisplay = downloadSizeDisplay,
            RetrievedAt = DateTimeOffset.UtcNow
        };
    }

    private static (long bytes, string display)? ParseSize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = SizeRegex().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var numberText = match.Groups[1].Value.Replace(',', '.').Trim();
        if (!double.TryParse(numberText, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var unit = match.Groups[2].Value.ToUpperInvariant();
        long multiplier = unit switch
        {
            "B" => 1,
            "KB" => 1024,
            "MB" => 1024 * 1024,
            "GB" => 1024L * 1024 * 1024,
            "TB" => 1024L * 1024 * 1024 * 1024,
            "BYTE" or "BYTES" => 1,
            _ => 1
        };

        var bytes = (long)(value * multiplier);
        var display = match.Value.Trim();

        return (bytes, display);
    }

    private static WingetMetadata ToModel(CacheEntry entry) =>
        new()
        {
            Id = entry.Id,
            Name = entry.Name,
            Version = entry.Version,
            Publisher = entry.Publisher,
            DownloadSizeBytes = entry.DownloadSizeBytes,
            DownloadSizeDisplay = entry.DownloadSizeDisplay,
            RetrievedAt = entry.RetrievedAt
        };

    private static CacheEntry ToCacheEntry(WingetMetadata metadata, string? source) =>
        new()
        {
            Id = metadata.Id,
            Name = metadata.Name,
            Version = metadata.Version,
            Publisher = metadata.Publisher,
            Source = source,
            DownloadSizeBytes = metadata.DownloadSizeBytes,
            DownloadSizeDisplay = metadata.DownloadSizeDisplay,
            RetrievedAt = metadata.RetrievedAt
        };

    [GeneratedRegex(@"(?i)\b(\d+(?:[.,]\d+)?)\s*(KB|MB|GB|TB|bytes?|b)\b")]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])")]
    private static partial Regex AnsiEscapeRegex();

    private sealed class CacheFile
    {
        public List<CacheEntry> Entries { get; set; } = [];
    }

    private sealed class CacheEntry
    {
        public string Id { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Source { get; set; }
        public string? Publisher { get; set; }
        public long? DownloadSizeBytes { get; set; }
        public string? DownloadSizeDisplay { get; set; }
        public DateTimeOffset RetrievedAt { get; set; }
    }
}
