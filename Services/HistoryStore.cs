using System.Text.Json;
using Asa.Models;

namespace Asa.Services;

/// <summary>
/// Armazena o histórico de lotes de operações em <c>%LOCALAPPDATA%\Asa\history.json</c>,
/// mantendo apenas as últimas 50 entradas.
/// </summary>
/// <remarks>
/// A gravação usa um arquivo temporário no mesmo diretório e substitui o arquivo final
/// por rename, para que uma falha no meio da escrita nunca deixe o histórico truncado.
/// Falhas de leitura/gravação são propagadas ao chamador (que decide como avisar o usuário)
/// em vez de serem silenciadas. Se o JSON estiver inválido, o arquivo original é movido
/// para um backup <c>history.corrupt-*.json</c> antes de qualquer nova gravação — o conteúdo
/// antigo nunca é apagado silenciosamente.
/// </remarks>
public sealed class HistoryStore
{
    private const int MaxEntries = 50;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _historyPath;
    private readonly object _lock = new();

    /// <summary>
    /// Disparado depois de qualquer gravação bem-sucedida no arquivo.
    /// Pode ser chamado em thread de thread pool; a UI deve despachar a atualização.
    /// </summary>
    public event EventHandler? Changed;

    public HistoryStore()
    {
        _historyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Asa",
            "history.json");
    }

    /// <summary>
    /// Adiciona uma entrada ao histórico, mantendo apenas as últimas <see cref="MaxEntries"/>.
    /// A tarefa só conclui após a gravação atômica terminar; falhas são propagadas.
    /// </summary>
    public Task SaveBatchAsync(HistoryEntry entry)
    {
        return Task.Run(() => SaveBatch(entry));
    }

    /// <summary>
    /// Retorna todas as entradas do histórico, mais recentes primeiro.
    /// </summary>
    public Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync()
    {
        return Task.Run(() => (IReadOnlyList<HistoryEntry>)GetHistory());
    }

    /// <summary>
    /// Marca o lote identificado por <paramref name="batchId"/> com os pacotes cuja
    /// atualização não foi confirmada. Atualiza pelo ID (e não pela posição) para não
    /// associar o aviso a um lote novo gravado no meio da verificação.
    /// Retorna <c>true</c> quando encontrou o lote e gravou alguma alteração.
    /// </summary>
    public Task<bool> MarkBatchStaleAsync(string batchId, IReadOnlyList<string> packageNames)
    {
        if (string.IsNullOrWhiteSpace(batchId) || packageNames.Count == 0)
        {
            return Task.FromResult(false);
        }

        return Task.Run(() => UpdateEntry(
            batchId,
            latest =>
            {
                var existing = latest.StaleUpgrades ?? [];
                var merged = existing
                    .Concat(packageNames)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();

                return merged.Length == 0 ? latest : CloneWithStaleUpgrades(latest, merged);
            }));
    }

    /// <summary>
    /// Remove o aviso de atualizações não confirmadas dos lotes informados.
    /// Chamado quando o usuário dispensa a <c>InfoBar</c> no Histórico: apenas as entradas
    /// que originaram o aviso exibido são limpas.
    /// Retorna <c>true</c> quando encontrou ao menos um lote e gravou alguma alteração.
    /// </summary>
    public Task<bool> ClearBatchesStaleAsync(IReadOnlyCollection<string> batchIds)
    {
        var targets = batchIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

        if (targets.Count == 0)
        {
            return Task.FromResult(false);
        }

        return Task.Run(() => ClearEntries(targets));
    }

    private void SaveBatch(HistoryEntry entry)
    {
        lock (_lock)
        {
            var entries = LoadEntriesLocked();

            entries.Insert(0, entry);
            if (entries.Count > MaxEntries)
            {
                entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
            }

            PersistLocked(entries);
        }

        NotifyChanged();
    }

    private bool UpdateEntry(string batchId, Func<HistoryEntry, HistoryEntry> update)
    {
        var changed = false;

        lock (_lock)
        {
            var entries = LoadEntriesLocked();
            var index = entries.FindIndex(entry =>
                string.Equals(entry.Identity, batchId, StringComparison.Ordinal));
            if (index < 0)
            {
                return false;
            }

            var updated = update(entries[index]);
            if (ReferenceEquals(updated, entries[index]))
            {
                return false;
            }

            entries[index] = updated;
            PersistLocked(entries);
            changed = true;
        }

        if (changed)
        {
            NotifyChanged();
        }

        return changed;
    }

    private bool ClearEntries(IReadOnlySet<string> batchIds)
    {
        var changed = false;

        lock (_lock)
        {
            var entries = LoadEntriesLocked();
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                if (entry.StaleUpgrades is not { Length: > 0 } ||
                    !batchIds.Contains(entry.Identity))
                {
                    continue;
                }

                entries[index] = CloneWithStaleUpgrades(entry, []);
                changed = true;
            }

            if (changed)
            {
                PersistLocked(entries);
            }
        }

        if (changed)
        {
            NotifyChanged();
        }

        return changed;
    }

    private static HistoryEntry CloneWithStaleUpgrades(HistoryEntry entry, string[] staleUpgrades) => new()
    {
        BatchId = entry.BatchId,
        Timestamp = entry.Timestamp,
        Operation = entry.Operation,
        ItemCount = entry.ItemCount,
        Items = entry.Items ?? [],
        ItemSources = entry.ItemSources ?? [],
        Result = entry.Result,
        Duration = entry.Duration,
        Source = entry.Source,
        RawLog = entry.RawLog,
        StaleUpgrades = staleUpgrades
    };

    private void NotifyChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private List<HistoryEntry> GetHistory()
    {
        lock (_lock)
        {
            return LoadEntriesLocked();
        }
    }

    private List<HistoryEntry> LoadEntriesLocked()
    {
        if (!File.Exists(_historyPath))
        {
            return [];
        }

        // Falhas de E/S (arquivo bloqueado, sem permissão) são propagadas: o chamador
        // precisa saber que o histórico não pôde ser lido, em vez de assumir lista vazia.
        var json = File.ReadAllText(_historyPath);

        HistoryFile? file;
        try
        {
            file = JsonSerializer.Deserialize<HistoryFile>(json, JsonOptions);
        }
        catch (JsonException)
        {
            PreserveUnreadableFileLocked();
            return [];
        }

        if (file is null)
        {
            PreserveUnreadableFileLocked();
            return [];
        }

        return (file.Entries ?? [])
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .ToList();
    }

    /// <summary>
    /// Move o arquivo inválido para um backup antes que uma nova gravação o substitua.
    /// Se o backup falhar, a exceção é propagada e o arquivo original permanece intacto.
    /// </summary>
    private void PreserveUnreadableFileLocked()
    {
        if (!File.Exists(_historyPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(_historyPath) ?? string.Empty;
        var backupName =
            $"{Path.GetFileNameWithoutExtension(_historyPath)}.corrupt-" +
            $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.json";
        var backupPath = Path.Combine(directory, backupName);

        File.Move(_historyPath, backupPath);
    }

    private void PersistLocked(List<HistoryEntry> entries)
    {
        var directory = Path.GetDirectoryName(_historyPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = Path.Combine(
            directory ?? string.Empty,
            $"{Path.GetFileName(_historyPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            // Grava o conteúdo completo em um temporário e só então substitui o arquivo
            // final (rename atômico no mesmo volume). O Flush(true) força a descida para
            // o disco antes do rename.
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                JsonSerializer.Serialize(stream, new HistoryFile { Entries = entries }, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, _historyPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Limpeza do temporário é best-effort; o erro original da gravação prevalece.
            }
        }
    }

    private sealed class HistoryFile
    {
        public List<HistoryEntry>? Entries { get; set; } = [];
    }
}
