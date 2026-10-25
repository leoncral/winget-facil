using System;
using System.Collections.ObjectModel;
using System.Text;
using Asa.Helpers;
using Asa.Models;
using Asa.Services;

namespace Asa.ViewModels;

/// <summary>
/// ViewModel da página Histórico (R-5). Lê os lotes persistidos por
/// <see cref="HistoryStore"/> (F1-9) e expõe os estados de lista, vazio e o aviso
/// de atualizações não confirmadas (MSIX) que sobrevive ao auto-hide.
/// </summary>
public sealed class HistoryViewModel : ObservableObject
{
    private readonly HistoryStore _store;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly HashSet<string> _warningBatchIds = new(StringComparer.Ordinal);

    private bool _isLoading;
    private string _msixWarningTitle = "Atualização não confirmada";
    private string? _msixWarningMessage;

    public HistoryViewModel(HistoryStore store)
    {
        _store = store;
    }

    public ObservableCollection<HistoryEntryViewModel> Entries { get; } = [];

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(ShowLoading));
                OnPropertyChanged(nameof(ShowEmpty));
            }
        }
    }

    /// <summary>Carga inicial: ainda não há nada para mostrar.</summary>
    public bool ShowLoading => IsLoading && Entries.Count == 0;

    /// <summary>Recarregando: a lista permanece visível (tokens, seção 6.7).</summary>
    public bool ShowList => Entries.Count > 0;

    public bool ShowEmpty => !IsLoading && Entries.Count == 0;

    public bool CanExport => Entries.Count > 0;

    public bool MsixWarningOpen => !string.IsNullOrWhiteSpace(_msixWarningMessage);

    public string MsixWarningTitle
    {
        get => _msixWarningTitle;
        private set => SetProperty(ref _msixWarningTitle, value);
    }

    public string? MsixWarningMessage
    {
        get => _msixWarningMessage;
        private set
        {
            if (SetProperty(ref _msixWarningMessage, value))
            {
                OnPropertyChanged(nameof(MsixWarningOpen));
            }
        }
    }

    public async Task LoadAsync()
    {
        await _loadGate.WaitAsync();
        try
        {
            IsLoading = true;

            // Preserva o estado expandido de logs entre recargas (ex.: lote novo salvo).
            var expandedKeys = Entries
                .Where(entry => entry.IsExpanded)
                .Select(entry => entry.ExpansionKey)
                .ToHashSet(StringComparer.Ordinal);

            var history = await _store.GetHistoryAsync();

            Entries.Clear();
            foreach (var entry in history)
            {
                var item = new HistoryEntryViewModel(entry);
                if (expandedKeys.Contains(item.ExpansionKey))
                {
                    item.IsExpanded = true;
                }

                Entries.Add(item);
            }

            UpdateMsixWarning(history);

            OnPropertyChanged(nameof(ShowList));
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(CanExport));
        }
        finally
        {
            IsLoading = false;
            _loadGate.Release();
        }
    }

    /// <summary>
    /// Dispensa a InfoBar de MSIX e remove os avisos dos lotes que a originaram no histórico,
    /// para que não reapareçam na próxima abertura. Lotes mais antigos ainda não dispensados
    /// continuam visíveis até que o usuário feche este aviso.
    /// </summary>
    public async Task DismissMsixWarningAsync()
    {
        MsixWarningMessage = null;

        var batchIds = _warningBatchIds.ToArray();
        _warningBatchIds.Clear();
        if (batchIds.Length == 0)
        {
            return;
        }

        try
        {
            await _store.ClearBatchesStaleAsync(batchIds);
        }
        catch
        {
            // Dispensar o aviso nunca deve falhar visivelmente; se a gravação falhar,
            // o aviso volta na próxima carga em vez de ser perdido em silêncio.
        }
    }

    /// <summary>Texto completo exportado pelo botão "Exportar log" (todos os lotes).</summary>
    public string BuildExportText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Asa · Histórico de operações");
        builder.AppendLine($"Exportado em {DateTimeOffset.Now:dd/MM/yyyy HH:mm}");
        builder.AppendLine($"Lotes registrados: {Entries.Count}");
        builder.AppendLine();

        foreach (var entry in Entries)
        {
            builder.AppendLine(new string('=', 60));
            builder.AppendLine($"{entry.TimestampDisplay} · {entry.Operation}");
            builder.AppendLine($"Itens ({entry.ItemCount}): {entry.ItemsListDisplay}");
            builder.AppendLine($"Resultado: {entry.ResultFullLabel}");
            builder.AppendLine($"Duração: {entry.DurationDisplay}");
            builder.AppendLine($"Origem: {entry.SourceDisplay}");

            if (entry.HasStaleUpgrades)
            {
                builder.AppendLine(
                    $"Atualizações não confirmadas: {string.Join(", ", entry.StaleUpgrades)}");
            }

            builder.AppendLine();
            builder.AppendLine(entry.LogText);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private void UpdateMsixWarning(IReadOnlyList<HistoryEntry> history)
    {
        // O aviso pode pertencer a mais de um lote ainda não dispensado. Considerar apenas
        // history[0] faria um aviso antigo desaparecer quando um lote novo (sem aviso)
        // fosse gravado. Agrega todos os lotes com StaleUpgrades e guarda seus IDs para
        // que o fechamento da InfoBar limpe exatamente as entradas exibidas.
        _warningBatchIds.Clear();

        var names = new List<string>();
        var seenNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

        foreach (var entry in history)
        {
            if (entry.StaleUpgrades is not { Length: > 0 })
            {
                continue;
            }

            _warningBatchIds.Add(entry.Identity);

            foreach (var name in entry.StaleUpgrades)
            {
                var trimmed = name?.Trim();
                if (!string.IsNullOrEmpty(trimmed) && seenNames.Add(trimmed))
                {
                    names.Add(trimmed);
                }
            }
        }

        if (names.Count == 0)
        {
            MsixWarningTitle = "Atualização não confirmada";
            MsixWarningMessage = null;
            return;
        }

        if (names.Count == 1)
        {
            MsixWarningTitle = "Atualização não confirmada";
            MsixWarningMessage =
                $"{names[0]} continua na versão antiga. Feche o aplicativo " +
                "(ou reinicie o Explorer/Windows) e tente novamente.";
            return;
        }

        MsixWarningTitle = $"{names.Count} atualizações não confirmadas";
        MsixWarningMessage =
            $"{string.Join(", ", names)} continuam na versão antiga. " +
            "Feche esses aplicativos (ou reinicie o Explorer/Windows) e tente novamente.";
    }
}
