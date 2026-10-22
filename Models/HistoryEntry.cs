namespace Asa.Models;

/// <summary>
/// Entrada de histórico de um lote de operações (instalação ou atualização).
/// </summary>
public sealed class HistoryEntry
{
    public DateTimeOffset Timestamp { get; init; }

    public string Operation { get; init; } = string.Empty; // "Instalação" / "Atualização"

    public int ItemCount { get; init; }

    public string[] Items { get; init; } = []; // IDs dos pacotes

    public string Result { get; init; } = string.Empty; // "Sucesso", "Falhou", "Cancelado", etc.

    public TimeSpan Duration { get; init; }

    public string Source { get; init; } = string.Empty; // origem (winget, msstore, local)

    public string? RawLog { get; init; }
}
