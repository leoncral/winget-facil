namespace Asa.Models;

/// <summary>
/// Metadados de um pacote retornados por <c>winget show --id &lt;id&gt; --exact</c>.
/// O tamanho do download pode estar indisponível; nesse caso os campos de tamanho
/// ficam nulos (nunca se inventa um valor placeholder).
/// </summary>
public sealed class WingetMetadata
{
    public string Id { get; init; } = string.Empty;

    public string? Name { get; init; }

    public string? Version { get; init; }

    public string? Publisher { get; init; }

    /// <summary>Tamanho do download em bytes, ou null quando não disponível.</summary>
    public long? DownloadSizeBytes { get; init; }

    /// <summary>Texto amigável do tamanho (ex.: "50,2 MB"), ou null.</summary>
    public string? DownloadSizeDisplay { get; init; }

    public DateTimeOffset RetrievedAt { get; init; }
}
