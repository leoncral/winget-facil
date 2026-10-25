using System.Text.Json.Serialization;

namespace Asa.Models;

/// <summary>
/// Entrada de histórico de um lote de operações (instalação ou atualização).
/// </summary>
public sealed class HistoryEntry
{
    /// <summary>
    /// Identificador do lote, usado para atualizar o aviso de MSIX no lote correto mesmo
    /// quando outro lote é gravado entre a conclusão e a verificação das atualizações.
    /// Entradas antigas (schema anterior) podem vir sem ele; nesse caso <see cref="Identity"/>
    /// deriva uma chave estável do próprio conteúdo.
    /// </summary>
    public string BatchId { get; init; } = string.Empty;

    [JsonIgnore]
    public string Identity => !string.IsNullOrWhiteSpace(BatchId)
        ? BatchId
        : $"{Timestamp.UtcTicks}|{Operation}|{string.Join("\u001f", Items ?? [])}";

    public DateTimeOffset Timestamp { get; init; }

    public string Operation { get; init; } = string.Empty; // "Instalação" / "Atualização"

    public int ItemCount { get; init; }

    public string[] Items { get; init; } = []; // IDs dos pacotes

    /// <summary>
    /// Origem de cada item, na mesma ordem de <see cref="Items"/>. Vazio em entradas
    /// antigas; nesse caso a exibição usa <see cref="Source"/> como fallback.
    /// </summary>
    public string[]? ItemSources { get; init; } = [];

    public string Result { get; init; } = string.Empty; // "Sucesso", "Falhou", "Cancelado", etc.

    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Origem agregada do lote (winget, msstore, local ou lista quando o lote é misto).
    /// Mantido para compatibilidade com entradas antigas que não têm <see cref="ItemSources"/>.
    /// </summary>
    public string Source { get; init; } = string.Empty;

    public string? RawLog { get; init; }

    /// <summary>
    /// Nomes dos pacotes cuja atualização o winget reportou como bem-sucedida, mas que
    /// continuaram pendentes após o lote (ex.: MSIX bloqueado por aplicativo em uso).
    /// Vazio quando não houve aviso. Persistido para que a página Histórico (R-5) possa
    /// exibir o aviso em <c>InfoBar</c> mesmo depois de reiniciar o app.
    /// </summary>
    public string[]? StaleUpgrades { get; init; } = [];
}
