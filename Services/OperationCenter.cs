using System.Text.RegularExpressions;
using Asa.Helpers;
using Asa.Models;
using Microsoft.UI.Dispatching;

namespace Asa.Services;

public sealed partial class OperationCenter : ObservableObject
{
    private const int MaxLogEntries = 600;
    private static readonly TimeSpan StatusThrottle = TimeSpan.FromMilliseconds(220);
    private readonly WingetCli _winget;
    private readonly SettingsStore _settings;
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly Queue<PackageOperation> _queue = new();
    private readonly List<PackageOperation> _completedUpgrades = [];
    private readonly List<string> _log = [];
    private readonly object _logLock = new();
    private PackageOperation? _current;
    private DateTimeOffset _lastStatusUpdate;
    private bool _isBusy;
    private bool _isActivityVisible;
    private bool _hasError;
    private bool _hasWarning;
    private string _activityTitle = string.Empty;
    private string _activityDetail = string.Empty;
    private int _batchTotal;
    private int _batchDone;
    private int _batchSucceeded;
    private int _batchFailed;

    public OperationCenter(WingetCli winget, SettingsStore settings)
    {
        _winget = winget;
        _settings = settings;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool IsActivityVisible
    {
        get => _isActivityVisible;
        private set => SetProperty(ref _isActivityVisible, value);
    }

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    public bool HasWarning
    {
        get => _hasWarning;
        private set => SetProperty(ref _hasWarning, value);
    }

    public string ActivityTitle
    {
        get => _activityTitle;
        private set => SetProperty(ref _activityTitle, value);
    }

    public string ActivityDetail
    {
        get => _activityDetail;
        private set => SetProperty(ref _activityDetail, value);
    }

    public int PendingCount => _queue.Count + (_current is null ? 0 : 1);

    /// <summary>Upgrades that the winget reported as successful in the current batch.</summary>
    public IReadOnlyList<PackageOperation> CompletedUpgrades => _completedUpgrades;

    public event EventHandler? QueueCompleted;

    /// <summary>
    /// Notifica o ciclo de vida de um item da fila (enfileirado → executando → concluído/falhou)
    /// com o identificador do pacote, para que cada tela possa refletir o estado na própria linha.
    /// Sempre disparada na UI thread.
    /// </summary>
    public event EventHandler<OperationItemEventArgs>? ItemStateChanged;

    public bool EnqueueInstall(SearchPackage package)
    {
        return Enqueue(new PackageOperation(
            package.Name,
            package.Id,
            package.Version,
            package.Source,
            PackageOperationKind.Install));
    }

    public int EnqueueUpgrades(IEnumerable<InstalledPackage> packages)
    {
        var count = 0;
        foreach (var package in packages)
        {
            if (Enqueue(new PackageOperation(
                    package.Name,
                    package.Id,
                    package.AvailableVersion,
                    package.Source,
                    PackageOperationKind.Upgrade)))
            {
                count++;
            }
        }

        return count;
    }

    public bool EnqueueUpgrade(InstalledPackage package)
    {
        return Enqueue(new PackageOperation(
            package.Name,
            package.Id,
            package.AvailableVersion,
            package.Source,
            PackageOperationKind.Upgrade));
    }

    public bool Contains(string packageId)
    {
        return (_current is not null && string.Equals(_current.Id, packageId, StringComparison.OrdinalIgnoreCase)) ||
               _queue.Any(operation => string.Equals(operation.Id, packageId, StringComparison.OrdinalIgnoreCase));
    }

    public void DismissActivity()
    {
        if (!IsBusy)
        {
            IsActivityVisible = false;
        }
    }

    /// <summary>
    /// Called after a batch when winget reported success but the packages still show an update.
    /// </summary>
    public void ReportStaleUpgrades(IReadOnlyList<string> packageNames)
    {
        if (packageNames.Count == 0)
        {
            return;
        }

        HasWarning = true;
        IsActivityVisible = true;

        if (packageNames.Count == 1)
        {
            ActivityTitle = "Atualização não confirmada";
            ActivityDetail = $"{packageNames[0]} continua na versão antiga. Feche o aplicativo (ou reinicie o Explorer/Windows) e tente novamente.";
        }
        else
        {
            ActivityTitle = $"{packageNames.Count} atualizações não confirmadas";
            ActivityDetail = $"Ainda pendentes: {string.Join(", ", packageNames)}. Feche esses aplicativos e tente novamente.";
        }

        AddLog($"Aviso: {ActivityDetail}");
    }

    public string GetLogText()
    {
        lock (_logLock)
        {
            return _log.Count == 0
                ? "Nenhum detalhe registrado ainda."
                : string.Join(Environment.NewLine, _log);
        }
    }

    private bool Enqueue(PackageOperation operation)
    {
        if (Contains(operation.Id))
        {
            return false;
        }

        var wasIdle = !IsBusy && _queue.Count == 0;
        if (wasIdle)
        {
            ResetBatch();
        }

        _queue.Enqueue(operation);
        _batchTotal++;

        // Notificação por ID: a linha correspondente passa a exibir "Na fila".
        RaiseItemState(operation, PackageOperationState.Queued, string.Empty);

        IsActivityVisible = true;
        if (wasIdle)
        {
            HasError = false;
            ActivityTitle = "Preparando…";
            ActivityDetail = "Aguardando o winget.";
        }

        if (!IsBusy)
        {
            StartProcessing();
        }

        return true;
    }

    private void ResetBatch()
    {
        _batchTotal = 0;
        _batchDone = 0;
        _batchSucceeded = 0;
        _batchFailed = 0;
        _completedUpgrades.Clear();
        HasError = false;
        HasWarning = false;

        lock (_logLock)
        {
            _log.Clear();
            _log.Add($"Asa · {DateTime.Now:dd/MM/yyyy HH:mm}");
        }
    }

    private void StartProcessing()
    {
        _ = ProcessQueueAsync();
    }

    private async Task ProcessQueueAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;

        while (_queue.Count > 0)
        {
            var operation = _queue.Dequeue();
            _current = operation;
            _lastStatusUpdate = DateTimeOffset.MinValue;
            var position = _batchDone + 1;

            ActivityTitle = $"{operation.ActionLabel} {operation.Name}";
            ActivityDetail = position == _batchTotal
                ? operation.Id
                : $"{operation.Id} · {position} de {_batchTotal}";

            RaiseItemState(operation, PackageOperationState.Running, string.Empty);

            AddLog(string.Empty);
            AddLog($"── {operation.ActionLabel} {operation.Name} · {operation.Id} ──");

            WingetCommandResult result;
            try
            {
                result = await _winget.ExecuteOperationAsync(
                    operation,
                    _settings.SilentInstall,
                    line => HandleOutputLine(operation, line));
            }
            catch (Exception exception)
            {
                result = new WingetCommandResult(-1, string.Empty, exception.Message, false, string.Empty);
            }

            _batchDone++;

            if (result.IsSuccess)
            {
                _batchSucceeded++;
                if (operation.Kind == PackageOperationKind.Upgrade)
                {
                    _completedUpgrades.Add(operation);
                }

                AddLog($"Concluído (código 0).");
                RaiseItemState(operation, PackageOperationState.Succeeded, string.Empty);
            }
            else
            {
                _batchFailed++;
                AddLog($"Falhou (código {result.ExitCodeDisplay}).");
                var errorText = FirstNonEmpty(result.Error, result.Output);
                if (!string.IsNullOrWhiteSpace(errorText))
                {
                    AddLog(errorText.Trim());
                }

                // A mesma mensagem já usada no log vira o tooltip de falha da linha.
                var failureMessage = string.IsNullOrWhiteSpace(errorText)
                    ? $"Falhou (código {result.ExitCodeDisplay})."
                    : errorText.Trim();
                RaiseItemState(operation, PackageOperationState.Failed, Truncate(failureMessage, 240));
            }
        }

        _current = null;
        IsBusy = false;
        CompleteBatch();
    }

    private void CompleteBatch()
    {
        if (_batchFailed == 0)
        {
            HasError = false;
            ActivityTitle = _batchTotal == 1 ? "Tudo pronto!" : $"{_batchSucceeded} tarefas concluídas";
            ActivityDetail = _batchTotal == 1
                ? "A operação foi concluída com sucesso."
                : "As operações foram concluídas com sucesso.";
        }
        else if (_batchSucceeded > 0)
        {
            HasError = true;
            ActivityTitle = "Algumas tarefas falharam";
            ActivityDetail = $"{_batchSucceeded} concluída(s) · {_batchFailed} com falha. Abra os detalhes para ver o log.";
        }
        else
        {
            HasError = true;
            ActivityTitle = _batchTotal == 1 ? "Não foi possível concluir" : "As tarefas falharam";
            ActivityDetail = "Abra os detalhes para ver o que o winget informou.";
        }

        IsActivityVisible = true;
        QueueCompleted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Publica o estado de um item da fila na UI thread (os await da fila podem retornar
    /// fora dela) para que as linhas correspondentes sejam atualizadas com segurança.
    /// </summary>
    private void RaiseItemState(PackageOperation operation, PackageOperationState state, string message)
    {
        void Raise()
        {
            ItemStateChanged?.Invoke(
                this,
                new OperationItemEventArgs(operation.Id, state, operation.Kind, message));
        }

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(Raise);
        }
        else
        {
            Raise();
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "…";
    }

    private void HandleOutputLine(PackageOperation operation, string rawLine)
    {
        var line = CleanLine(rawLine);
        if (line.Length == 0)
        {
            return;
        }

        AddLog(line);

        if (DateTimeOffset.UtcNow - _lastStatusUpdate < StatusThrottle)
        {
            return;
        }

        _lastStatusUpdate = DateTimeOffset.UtcNow;

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => UpdateTransientStatus(operation, line));
        }
        else
        {
            UpdateTransientStatus(operation, line);
        }
    }

    private void UpdateTransientStatus(PackageOperation operation, string line)
    {
        if (!IsBusy || !ReferenceEquals(_current, operation))
        {
            return;
        }

        if (line.Length > 150)
        {
            line = line[..150] + "…";
        }

        ActivityDetail = line;
    }

    private void AddLog(string line)
    {
        lock (_logLock)
        {
            _log.Add(line);
            if (_log.Count > MaxLogEntries)
            {
                _log.RemoveRange(0, _log.Count - MaxLogEntries);
            }
        }
    }

    private static string CleanLine(string value)
    {
        var withoutAnsi = AnsiEscapeRegex().Replace(value, string.Empty)
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Replace("\b", string.Empty, StringComparison.Ordinal)
            .Trim();

        return withoutAnsi.Length > 0 && withoutAnsi.All(character => character is '-' or '=')
            ? string.Empty
            : withoutAnsi;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    [GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])")]
    private static partial Regex AnsiEscapeRegex();
}

/// <summary>Estado de um item dentro da fila do <see cref="OperationCenter"/>.</summary>
public enum PackageOperationState
{
    Queued,
    Running,
    Succeeded,
    Failed
}

/// <summary>
/// Argumentos de <see cref="OperationCenter.ItemStateChanged"/>: identifica o pacote pelo Id
/// (sem case sensitivity) para que cada ViewModel encontre a linha correspondente.
/// </summary>
public sealed class OperationItemEventArgs : EventArgs
{
    public OperationItemEventArgs(string packageId, PackageOperationState state, PackageOperationKind kind, string message)
    {
        PackageId = packageId;
        State = state;
        Kind = kind;
        Message = message;
    }

    /// <summary>Id do pacote (winget), usado para casar com a linha exibida.</summary>
    public string PackageId { get; }

    public PackageOperationState State { get; }

    /// <summary>Instalação ou atualização: define o texto "Instalando…" vs "Atualizando…".</summary>
    public PackageOperationKind Kind { get; }

    /// <summary>Mensagem de falha (vazia nos demais estados).</summary>
    public string Message { get; }
}
