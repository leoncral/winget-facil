using System.Text.Json;
using Asa.Models;

namespace Asa.Services;

/// <summary>
/// Armazena o histórico de lotes de operações em <c>%LOCALAPPDATA%\Asa\history.json</c>,
/// mantendo apenas as últimas 50 entradas. Nunca lança: arquivo ausente/corrompido
/// resulta em lista vazia.
/// </summary>
public sealed class HistoryStore
{
    private const int MaxEntries = 50;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _historyPath;
    private readonly object _lock = new();

    public HistoryStore()
    {
        _historyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Asa",
            "history.json");
    }

    /// <summary>
    /// Adiciona uma entrada ao histórico, mantendo apenas as últimas <see cref="MaxEntries"/>.
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

    private void SaveBatch(HistoryEntry entry)
    {
        lock (_lock)
        {
            try
            {
                var entries = LoadEntriesLocked();

                entries.Insert(0, entry);
                if (entries.Count > MaxEntries)
                {
                    entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
                }

                PersistLocked(entries);
            }
            catch
            {
                // O histórico nunca deve impedir o funcionamento do app.
            }
        }
    }

    private List<HistoryEntry> GetHistory()
    {
        lock (_lock)
        {
            try
            {
                return LoadEntriesLocked();
            }
            catch
            {
                return [];
            }
        }
    }

    private List<HistoryEntry> LoadEntriesLocked()
    {
        try
        {
            if (!File.Exists(_historyPath))
            {
                return [];
            }

            var json = File.ReadAllText(_historyPath);
            var file = JsonSerializer.Deserialize<HistoryFile>(json, JsonOptions) ?? new HistoryFile();

            return file.Entries.Where(e => e is not null).Select(e => e!).ToList();
        }
        catch
        {
            // Arquivo ausente ou corrompido: histórico vazio.
            return [];
        }
    }

    private void PersistLocked(List<HistoryEntry> entries)
    {
        try
        {
            var directory = Path.GetDirectoryName(_historyPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var file = new HistoryFile { Entries = entries };
            File.WriteAllText(_historyPath, JsonSerializer.Serialize(file, JsonOptions));
        }
        catch
        {
            // Ignorar falhas de gravação do histórico.
        }
    }

    private sealed class HistoryFile
    {
        public List<HistoryEntry> Entries { get; set; } = [];
    }
}
