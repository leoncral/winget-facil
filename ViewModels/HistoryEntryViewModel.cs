using System;
using Asa.Helpers;
using Asa.Models;

namespace Asa.ViewModels;

/// <summary>
/// Item da lista de lotes exibida na página Histórico (R-5). Formata data, itens,
/// resultado e duração a partir de <see cref="HistoryEntry"/> e controla o estado
/// expandido do log. Nunca inventa dados: campos ausentes viram texto neutro.
/// </summary>
public sealed class HistoryEntryViewModel : ObservableObject
{
    private bool _isExpanded;

    public HistoryEntryViewModel(HistoryEntry entry)
    {
        Entry = entry;
    }

    public HistoryEntry Entry { get; }

    public string Operation => string.IsNullOrWhiteSpace(Entry.Operation)
        ? "Operação"
        : Entry.Operation;

    public string TimestampDisplay => Entry.Timestamp.ToLocalTime().ToString("dd/MM/yyyy · HH:mm");

    public int ItemCount => Entry.ItemCount > 0 ? Entry.ItemCount : (Entry.Items?.Length ?? 0);

    public string ItemsDisplay => ItemCount == 1 ? "1 item" : $"{ItemCount} itens";

    public string DurationDisplay => FormatDuration(Entry.Duration);

    public string SourceBadge
    {
        get
        {
            var sources = SourceLabels;
            return sources.Count switch
            {
                0 => "desconhecida",
                1 => sources[0],
                _ => "mista"
            };
        }
    }

    /// <summary>Origem completa do lote para exportação (ex.: "winget, msstore").</summary>
    public string SourceDisplay => SourceLabels.Count == 0
        ? "desconhecida"
        : string.Join(", ", SourceLabels);

    private IReadOnlyList<string> SourceLabels
    {
        get
        {
            if (Entry.ItemSources is { Length: > 0 } itemSources)
            {
                var labels = itemSources
                    .Select(source =>
                    {
                        var normalized = NormalizeSource(source);
                        return normalized.Length == 0 ? "desconhecida" : normalized;
                    })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Quando todos os itens vieram sem origem, o agregado antigo ainda pode
                // trazer a informação; caso contrário, "desconhecida" é a resposta honesta.
                if (labels.Count > 1 ||
                    !labels[0].Equals("desconhecida", StringComparison.OrdinalIgnoreCase))
                {
                    return labels;
                }
            }

            var fallback = (Entry.Source ?? string.Empty).Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return fallback
                .Select(NormalizeSource)
                .Where(source => source.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private static string NormalizeSource(string? source)
    {
        var value = source?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (value.Equals("msstore", StringComparison.OrdinalIgnoreCase))
        {
            return "msstore";
        }

        if (value.Equals("local", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("ARP\\", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase))
        {
            return "local";
        }

        return value.Equals("winget", StringComparison.OrdinalIgnoreCase)
            ? "winget"
            : value;
    }

    /// <summary>Linha secundária: data/hora · itens · duração.</summary>
    public string DetailsDisplay => $"{TimestampDisplay} · {ItemsDisplay} · {DurationDisplay}";

    public string ResultFullLabel => string.IsNullOrWhiteSpace(Entry.Result)
        ? "Resultado não registrado"
        : Entry.Result;

    /// <summary>
    /// Versão curta para o chip (estilo badge, uppercase). O texto completo permanece
    /// no tooltip e no log exportado.
    /// </summary>
    public string ResultShortLabel
    {
        get
        {
            var result = Entry.Result?.Trim() ?? string.Empty;

            if (result.StartsWith("Sucesso", StringComparison.OrdinalIgnoreCase))
            {
                return result.Contains("ignorado", StringComparison.OrdinalIgnoreCase)
                    ? "IGNORADOS"
                    : "SUCESSO";
            }

            return result switch
            {
                _ when result.StartsWith("Parcial", StringComparison.OrdinalIgnoreCase) => "PARCIAL",
                _ when result.StartsWith("Cancel", StringComparison.OrdinalIgnoreCase) => "CANCELADO",
                _ when result.StartsWith("Falh", StringComparison.OrdinalIgnoreCase) => "FALHOU",
                _ when result.StartsWith("Erro", StringComparison.OrdinalIgnoreCase) => "ERRO",
                _ => string.IsNullOrEmpty(result)
                    ? "SEM RESULTADO"
                    : result.ToUpperInvariant(),
            };
        }
    }

    public bool HasLog => !string.IsNullOrWhiteSpace(Entry.RawLog);

    public string LogText => HasLog
        ? Entry.RawLog!.Trim()
        : "Nenhum log foi registrado para este lote.";

    public string[] Items => Entry.Items ?? [];

    public string[] StaleUpgrades => Entry.StaleUpgrades ?? [];

    public bool HasStaleUpgrades => StaleUpgrades.Length > 0;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ChevronGlyph));
                OnPropertyChanged(nameof(ExpandAutomationName));
            }
        }
    }

    public string ChevronGlyph => IsExpanded ? "\uE70E" : "\uE70D";

    public string ExpandAutomationName => IsExpanded
        ? $"Recolher o log do lote de {Operation.ToLowerInvariant()} de {TimestampDisplay}"
        : $"Expandir o log do lote de {Operation.ToLowerInvariant()} de {TimestampDisplay}";

    /// <summary>Chave estável usada para preservar o estado expandido ao recarregar o histórico.</summary>
    public string ExpansionKey => $"{Entry.Timestamp.UtcTicks}|{Entry.Operation}";

    /// <summary>Lista de IDs do lote para exibição/exportação.</summary>
    public string ItemsListDisplay => Items.Length == 0
        ? "IDs não registrados"
        : string.Join(", ", Items);

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return "menos de 1 s";
        }

        if (duration.TotalSeconds < 1)
        {
            return "menos de 1 s";
        }

        if (duration.TotalMinutes < 1)
        {
            return $"{(int)duration.TotalSeconds} s";
        }

        if (duration.TotalHours < 1)
        {
            return $"{(int)duration.TotalMinutes} min {duration.Seconds} s";
        }

        return $"{(int)duration.TotalHours} h {duration.Minutes} min";
    }
}
