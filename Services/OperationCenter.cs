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
    private readonly RunningAppGuard _runningApps;
    private readonly HistoryStore _history;
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly Queue<PackageOperation> _queue = new();
    private readonly List<PackageOperation> _completedUpgrades = [];
    private readonly List<string> _log = [];
    private readonly object _logLock = new();
    private readonly List<PackageOperation> _canceledBeforeStart = [];
    private int _canceledBeforeStartInstalls;
    private int _canceledBeforeStartUpgrades;
    private PackageOperation? _current;
    private CancellationTokenSource? _operationCts;
    private bool _cancelRequested;
    private bool _isCanceling;
    private bool _isCanceledActivity;
    private bool _isFinalizing;
    private DateTimeOffset _lastStatusUpdate;
    private bool _isBusy;
    private bool _isActivityVisible;
    private bool _hasError;
    private bool _hasWarning;
    private string _activityTitle = string.Empty;
    private string _activityDetail = string.Empty;
    private int? _progressPercent;
    private OperationPhase _currentPhase;
    private long _batchGeneration;
    private int _batchTotal;
    private int _batchDone;
    private int _batchSucceeded;
    private int _batchFailed;
    private int _batchSkipped;
    private DateTimeOffset _batchStartTime;

    public OperationCenter(
        WingetCli winget,
        SettingsStore settings,
        RunningAppGuard runningApps,
        HistoryStore history)
    {
        _winget = winget;
        _settings = settings;
        _runningApps = runningApps;
        _history = history;
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

    /// <summary>
    /// True quando a atividade exibida é um cancelamento (e não um aviso permanente):
    /// permite que a janela auto-oculte a barra mesmo usando o brush de atenção âmbar.
    /// </summary>
    public bool IsCanceledActivity
    {
        get => _isCanceledActivity;
        private set => SetProperty(ref _isCanceledActivity, value);
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

    /// <summary>
    /// Porcentagem de progresso da operação corrente (0–100). Null quando o winget não
    /// reporta um % (indeterminado: ProgressRing continua ativo).
    /// </summary>
    public int? ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    /// <summary>Fase da operação corrente: Downloading, Installing, Verifying ou None.</summary>
    public OperationPhase CurrentPhase
    {
        get => _currentPhase;
        private set => SetProperty(ref _currentPhase, value);
    }

    public int PendingCount => _queue.Count + (_current is null ? 0 : 1);

    /// <summary>Upgrades that the winget reported as successful in the current batch.</summary>
    public IReadOnlyList<PackageOperation> CompletedUpgrades => _completedUpgrades;

    /// <summary>
    /// Shows the permission dialog for closing a package that is currently in use.
    /// Returns true when the update may proceed.
    /// </summary>
    public Func<RunningAppUsage, Task<bool>>? ConfirmCloseRunningApp { get; set; }

    public event EventHandler<BatchCompletedEventArgs>? QueueCompleted;

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
                    PackageOperationKind.Upgrade,
                    package.Version)))
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
            PackageOperationKind.Upgrade,
            package.Version));
    }

    /// <summary>
    /// True quando o Id já tem operação ativa (na fila ou em execução). O winget executa
    /// upgrades por Id: duas linhas instaladas com o mesmo Id são o mesmo alvo de comando
    /// e não podem gerar duas operações no mesmo lote.
    /// </summary>
    public bool Contains(string packageId)
    {
        return (_current is not null && string.Equals(_current.Id, packageId, StringComparison.OrdinalIgnoreCase)) ||
               _queue.Any(operation => string.Equals(operation.Id, packageId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Operação ativa correspondente à instalação informada. Para upgrades a identidade
    /// inclui a versão instalada e a origem, de modo que um evento de estado não seja
    /// atribuído à linha errada quando o mesmo Id aparece em versões lado a lado.
    /// Instalações não têm versão instalada de origem: a origem ainda faz parte da
    /// identidade do alvo (o winget instala a partir de uma origem), mas versões lado a
    /// lado da mesma origem continuam indistinguíveis. A origem é sempre comparada
    /// exatamente: vazia significa "desconhecida", nunca "qualquer origem", e não restaura
    /// estado em nenhuma linha. Quando a identidade não permite distinguir a linha, o estado
    /// terminal só é pintado se houver uma única candidata; as demais são liberadas pelo
    /// <see cref="PackagesViewModel"/> em vez de permanecerem presas em Queued.
    /// </summary>
    public PackageOperation? FindActiveOperation(string packageId, string installedVersion, string source)
    {
        if (_current is not null && Matches(_current, packageId, installedVersion, source))
        {
            return _current;
        }

        foreach (var operation in _queue)
        {
            if (Matches(operation, packageId, installedVersion, source))
            {
                return operation;
            }
        }

        return null;
    }

    private static bool Matches(
        PackageOperation operation,
        string packageId,
        string installedVersion,
        string source)
    {
        if (!string.Equals(operation.Id, packageId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (operation.Kind != PackageOperationKind.Upgrade)
        {
            // Instalação: o comando identifica o alvo por Id + origem. Sem versão instalada
            // de origem não há como distinguir versões lado a lado; a origem ao menos evita
            // marcar uma instalação de outra origem com o mesmo Id. Uma operação sem origem
            // (desconhecida) não pode funcionar como curinga nem identificar a linha de
            // origem: nenhuma linha deve ser restaurada como "Na fila".
            return SameOrigin(operation.Source, source);
        }

        // Upgrades sempre carregam a versão instalada de origem; comparar também a origem
        // evita casar duas instalações distintas que compartilham Id e versão. Sem versão
        // instalada a identidade está incompleta: não restaura estado em linha nenhuma.
        var normalizedInstalledVersion = operation.InstalledVersion?.Trim();
        var normalizedCandidateVersion = installedVersion?.Trim();

        return !string.IsNullOrEmpty(normalizedInstalledVersion) &&
               !string.IsNullOrEmpty(normalizedCandidateVersion) &&
               string.Equals(
                   normalizedInstalledVersion,
                   normalizedCandidateVersion,
                   StringComparison.OrdinalIgnoreCase) &&
               SameOrigin(operation.Source, source);
    }

    /// <summary>
    /// Compara origens normalizando espaços. Origem vazia é desconhecida: não identifica
    /// linha nenhuma e nunca casa com outra origem (nem com outra vazia), pois duas origens
    /// desconhecidas não provam ser a mesma.
    /// </summary>
    private static bool SameOrigin(string? operationSource, string? installSource)
    {
        var normalizedOperationSource = operationSource?.Trim();
        var normalizedInstallSource = installSource?.Trim();

        return !string.IsNullOrEmpty(normalizedOperationSource) &&
               !string.IsNullOrEmpty(normalizedInstallSource) &&
               string.Equals(
                   normalizedOperationSource,
                   normalizedInstallSource,
                   StringComparison.OrdinalIgnoreCase);
    }

    public void DismissActivity()
    {
        if (!IsBusy)
        {
            IsActivityVisible = false;
        }
    }

    /// <summary>
    /// Interrompe a operação em andamento: encerra o processo do winget atual (e sua árvore
    /// de processos filhos) e descarta todos os itens ainda na fila, que não serão executados.
    /// A operação corrente passa a <see cref="PackageOperationState.Canceled"/> (não é uma
    /// falha) e o laço <see cref="ProcessQueueAsync"/> termina sem partir para o próximo item.
    /// No-op seguro quando não há nada em execução (inclusive em cliques duplicados).
    /// </summary>
    public async Task CancelCurrentAsync()
    {
        if (!IsBusy || _isCanceling || _cancelRequested || _isFinalizing)
        {
            return;
        }

        _isCanceling = true;
        try
        {
            // Precisa ser sinalizado antes do kill: quando o laço acordar, ele já trata o
            // resultado da operação corrente como cancelada (nunca como falha comum).
            _cancelRequested = true;

            // Itens que ainda não começaram nunca serão executados: voltam direto para Idle.
            // Também contamos se eram instalações ou atualizações para o rótulo do lote misto.
            while (_queue.Count > 0)
            {
                var pending = _queue.Dequeue();
                _canceledBeforeStart.Add(pending);
                if (pending.Kind == PackageOperationKind.Install)
                {
                    _canceledBeforeStartInstalls++;
                }
                else if (pending.Kind == PackageOperationKind.Upgrade)
                {
                    _canceledBeforeStartUpgrades++;
                }
                RaiseItemState(pending, PackageOperationState.Canceled, string.Empty, canceledBeforeStart: true);
            }

            // Encerra a árvore de processos do winget em execução (com espera limitada).
            await _winget.KillCurrentProcessAsync().ConfigureAwait(true);

            // Acorda o WaitForExitAsync e os leitores de saída do WingetCli, se ainda ativos.
            try
            {
                _operationCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // O processo terminou exatamente agora; o próprio laço finaliza o item.
            }

            if (_current is not null)
            {
                AddLog(string.Empty);
                AddLog($"Comando interrompido: {_current.ActionLabel} {_current.Name} · {_current.Id}");
            }

            AddLog("Operação cancelada pelo usuário.");
        }
        finally
        {
            _isCanceling = false;
        }
    }

    /// <summary>
    /// Publica o aviso de atualizações não confirmadas somente se o lote identificado por
    /// <paramref name="batchGeneration"/> ainda for o lote mais recente. Verificar apenas
    /// <see cref="IsBusy"/> não basta: um lote posterior pode começar e terminar durante os
    /// awaits da verificação, deixando <see cref="IsBusy"/> falso novamente. Nesse caso o
    /// aviso não pode substituir o status/atividade do lote novo (a persistência no
    /// histórico é feita pelo chamador, independentemente do retorno).
    /// Retorna <c>true</c> quando a barra de atividade foi atualizada.
    /// </summary>
    public bool TryReportStaleUpgrades(IReadOnlyList<string> packageNames, long batchGeneration)
    {
        if (packageNames.Count == 0 ||
            IsBusy ||
            batchGeneration != _batchGeneration)
        {
            return false;
        }

        PublishStaleUpgrades(packageNames);
        return true;
    }

    /// <summary>
    /// Called after a batch when winget reported success but the packages still show an update.
    /// </summary>
    private void PublishStaleUpgrades(IReadOnlyList<string> packageNames)
    {
        // Um aviso de "atualização não confirmada" é permanente (não auto-oculta como o
        // cancelamento), então o estado de cancelamento não deve valer aqui.
        IsCanceledActivity = false;
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

    /// <summary>
    /// Registra no log da atividade uma falha não fatal ao persistir o histórico, sem
    /// interromper a operação que já terminou.
    /// </summary>
    public void ReportHistoryPersistenceFailure(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        AddLog($"Não foi possível atualizar o aviso no histórico: {message}");
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
        // Nova geração a cada lote: um evento de conclusão antigo não pode publicar avisos
        // na barra de atividade de um lote iniciado depois dele.
        _batchGeneration++;
        _batchStartTime = DateTimeOffset.Now;
        _batchTotal = 0;
        _batchDone = 0;
        _batchSucceeded = 0;
        _batchFailed = 0;
        _batchSkipped = 0;
        _completedUpgrades.Clear();
        HasError = false;
        HasWarning = false;
        IsCanceledActivity = false;
        _cancelRequested = false;
        _canceledBeforeStart.Clear();
        _canceledBeforeStartInstalls = 0;
        _canceledBeforeStartUpgrades = 0;

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

        // True quando a ÚLTIMA operação finalizada foi interrompida pelo usuário: a barra de
        // atividade mostra "Cancelado" em vez do resumo comum de concluído/falhou.
        var lastFinalizedCanceled = false;
        // True se o cancelamento foi solicitado a qualquer momento durante o lote: mesmo que
        // o último item tenha terminado com sucesso (corrida com o encerramento do winget),
        // o lote foi cancelado e o resultado deve refletir isso.
        var cancelRequestedDuringBatch = false;
        var hasInstalls = false;
        var hasUpgrades = false;
        var processedOperations = new List<PackageOperation>();

        while (_queue.Count > 0)
        {
            var operation = _queue.Dequeue();
            if (operation.Kind == PackageOperationKind.Install)
            {
                hasInstalls = true;
            }
            else if (operation.Kind == PackageOperationKind.Upgrade)
            {
                hasUpgrades = true;
            }
            processedOperations.Add(operation);
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

            var stoppedServices = new List<string>();
            var explorerClosed = false;
            var skipped = false;

            // Um CancellationTokenSource por item: cancelCurrentAsync cancela apenas a
            // execução em curso, sem afetar operações futuras enfileiradas depois.
            var operationCts = new CancellationTokenSource();
            _operationCts = operationCts;

            WingetCommandResult? result = null;
            try
            {
                if (operation.Kind == PackageOperationKind.Upgrade)
                {
                    try
                    {
                        RunningAppUsage? usage = null;
                        try
                        {
                            // Cancelado antes da detecção: nem varre os processos em execução.
                            operationCts.Token.ThrowIfCancellationRequested();

                            usage = await _runningApps.DetectAsync(operation.Name, operation.Id, operationCts.Token);

                            // Cancelado durante a detecção: não abrir o diálogo de confirmação
                            // de uma operação que já foi cancelada.
                            operationCts.Token.ThrowIfCancellationRequested();
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            AddLog($"Não foi possível verificar aplicativos em execução: {exception.Message}");
                        }

                        if (usage is { HasBlockers: true })
                        {
                            AddLog($"Em execução: {DescribeBlockers(usage)}.");

                            if (usage.HasCriticalProcesses)
                            {
                                skipped = true;
                                AddLog("Atualização ignorada: um processo crítico do Windows usa este pacote.");
                            }
                            else
                            {
                                ActivityTitle = $"Aguardando confirmação — {operation.Name}";
                                ActivityDetail = "O aplicativo está aberto e precisa ser fechado.";

                                var approved = false;
                                if (ConfirmCloseRunningApp is not null)
                                {
                                    try
                                    {
                                        approved = await ConfirmCloseRunningApp(usage);
                                    }
                                    catch (Exception exception)
                                    {
                                        AddLog($"Falha ao exibir a confirmação: {exception.Message}");
                                    }
                                }

                                // Cancelado enquanto o diálogo estava aberto: não fechar
                                // aplicativos de uma operação que já foi cancelada.
                                operationCts.Token.ThrowIfCancellationRequested();

                                if (!approved)
                                {
                                    skipped = true;
                                    AddLog("Atualização ignorada: o aplicativo permaneceu aberto.");
                                }
                                else
                                {
                                    ActivityTitle = $"Fechando {operation.Name}…";
                                    ActivityDetail = "Aguardando os processos e serviços encerrarem.";

                                    RunningAppCloseResult? closeResult = null;
                                    try
                                    {
                                        closeResult = await _runningApps.CloseAsync(usage, operationCts.Token);
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        throw;
                                    }
                                    catch (Exception exception)
                                    {
                                        AddLog($"Falha ao fechar o aplicativo: {exception.Message}");
                                    }

                                    if (closeResult is not null)
                                    {
                                        stoppedServices.AddRange(closeResult.StoppedServices);
                                        explorerClosed = closeResult.ExplorerClosed;

                                        foreach (var service in closeResult.StoppedServices)
                                        {
                                            AddLog($"Serviço parado: {service}.");
                                        }

                                        if (!closeResult.Success)
                                        {
                                            foreach (var failure in closeResult.Failures)
                                            {
                                                AddLog($"Não foi possível fechar: {failure}.");
                                            }

                                            skipped = true;
                                            AddLog("Atualização ignorada: não foi possível liberar o aplicativo.");
                                        }
                                    }
                                    else
                                    {
                                        skipped = true;
                                        AddLog("Atualização ignorada: não foi possível liberar o aplicativo.");
                                    }

                                    if (!skipped)
                                    {
                                        AddLog("Aplicativo liberado para atualização.");
                                    }
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancelamento na fase de guarda (detecção/fechamento): o item segue
                        // pelo caminho de cancelamento já existente — "skipped" e "result"
                        // continuam vazios, então fica Canceled sem contar como falha, e o
                        // finally acima ainda reinicia os serviços que foram parados.
                    }
                }

                if (!skipped)
                {
                    ActivityTitle = $"{operation.ActionLabel} {operation.Name}";
                    ActivityDetail = position == _batchTotal
                        ? operation.Id
                        : $"{operation.Id} · {position} de {_batchTotal}";

                    if (_cancelRequested)
                    {
                        AddLog("Atualização cancelada antes de iniciar o winget.");
                    }
                    else
                    {
                        // Novo item da operação: reseta progresso/fase antes de iniciar o winget.
                        ProgressPercent = null;
                        CurrentPhase = OperationPhase.None;

                        try
                        {
                            result = await _winget.ExecuteOperationAsync(
                                operation,
                                _settings.SilentInstall,
                                line => HandleOutputLine(operation, line),
                                operationCts.Token);
                        }
                        catch (Exception exception)
                        {
                            result = new WingetCommandResult(-1, string.Empty, exception.Message, false, string.Empty);
                        }
                    }
                }
            }
            finally
            {
                // Serviços e Explorer precisam voltar mesmo se o winget falhar ou o lote
                // for cancelado depois que o aplicativo já foi fechado.
                await RestartServicesAsync(stoppedServices);

                if (explorerClosed)
                {
                    _runningApps.EnsureExplorerRunning();
                }

                if (ReferenceEquals(_operationCts, operationCts))
                {
                    _operationCts = null;
                }

                operationCts.Dispose();
            }

            // Consome o pedido de cancelamento deste item: um pedido novo durante o lote
            // seguinte volta a valer para a próxima execução.
            var wasCanceled = _cancelRequested;
            _cancelRequested = false;
            if (wasCanceled)
            {
                cancelRequestedDuringBatch = true;
            }

            _batchDone++;

            if (skipped)
            {
                _batchSkipped++;
                if (wasCanceled)
                {
                    lastFinalizedCanceled = true;
                }

                // Não é falha nem cancelamento do winget: a linha volta ao estado normal.
                RaiseItemState(operation, PackageOperationState.Canceled, string.Empty, canceledBeforeStart: true);
                continue;
            }

            if (result is null)
            {
                // O usuário cancelou enquanto o aplicativo estava sendo liberado: o winget
                // nunca foi iniciado e os serviços já voltaram no finally acima.
                lastFinalizedCanceled = true;
                RaiseItemState(operation, PackageOperationState.Canceled, string.Empty);
                continue;
            }

            if (result.IsSuccess)
            {
                // Corrida com o cancelamento: o winget terminou com sucesso antes do kill,
                // então o item é concluído — nunca "cancelado".
                _batchSucceeded++;
                if (operation.Kind == PackageOperationKind.Upgrade)
                {
                    _completedUpgrades.Add(operation);
                }

                AddLog($"Concluído (código 0).");
                RaiseItemState(operation, PackageOperationState.Succeeded, string.Empty);
            }
            else if (wasCanceled || result.WasCanceled)
            {
                // Cancelado pelo usuário: não é falha e não conta no resumo de erros.
                lastFinalizedCanceled = true;
                RaiseItemState(operation, PackageOperationState.Canceled, string.Empty);
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

        // Itens cancelados antes de começar (dequeuados em CancelCurrentAsync) também fazem
        // parte do lote e entram no histórico.
        processedOperations.AddRange(_canceledBeforeStart);

        // Itens cancelados antes de começar também contam para decidir se o lote foi misto
        // (instalação + atualização) e, portanto, qual rótulo usar no histórico.
        if (_canceledBeforeStartInstalls > 0)
        {
            hasInstalls = true;
        }
        if (_canceledBeforeStartUpgrades > 0)
        {
            hasUpgrades = true;
        }

        // O lote é cancelado se o cancelamento foi solicitado a qualquer momento, mesmo que
        // o último item tenha terminado com sucesso (corrida com o encerramento do winget).
        var finalCanceled = lastFinalizedCanceled || cancelRequestedDuringBatch;

        // Snapshot do lote antes de qualquer await: IsBusy continua true durante a gravação,
        // mas itens enfileirados nesse intervalo pertencem ao PRÓXIMO lote e não podem
        // contaminar o resumo/ID deste.
        var completedUpgrades = _completedUpgrades.ToArray();
        var completedTotal = _batchTotal;
        var completedGeneration = _batchGeneration;

        // Persiste a origem de cada item: o badge do histórico não pode assumir "winget"
        // quando o lote é misto ou veio da msstore.
        var itemSources = processedOperations
            .Select(operation => operation.Source?.Trim() ?? string.Empty)
            .ToArray();

        // F1-9: grava o lote antes de a operação ser dada como concluída. Se o histórico
        // falhar, a operação continua, mas o problema fica registrado no log da atividade.
        var entry = new HistoryEntry
        {
            BatchId = Guid.NewGuid().ToString("N"),
            Timestamp = DateTimeOffset.Now,
            Operation = hasInstalls && hasUpgrades
                ? "Instalação e atualização"
                : hasInstalls ? "Instalação"
                : "Atualização",
            ItemCount = completedTotal,
            Items = processedOperations.Select(operation => operation.Id).ToArray(),
            ItemSources = itemSources,
            Result = finalCanceled
                ? "Cancelado"
                : _batchFailed > 0 && _batchSucceeded > 0
                    ? "Parcial"
                    : _batchFailed > 0
                        ? "Falhou"
                        : _batchSkipped > 0
                            ? "Sucesso com itens ignorados"
                            : "Sucesso",
            Duration = DateTimeOffset.Now - _batchStartTime,
            Source = BuildSourceSummary(itemSources),
            RawLog = GetLogText()
        };

        string? historyBatchId = null;
        _isFinalizing = true;
        try
        {
            await _history.SaveBatchAsync(entry);
            historyBatchId = entry.BatchId;
        }
        catch (Exception exception)
        {
            AddLog($"Não foi possível salvar o histórico: {exception.Message}");
        }
        finally
        {
            // O lote já terminou; um cancelamento nesse intervalo seria um no-op e não
            // pode mover para _canceledBeforeStart itens que já pertencem ao próximo lote.
            _isFinalizing = false;
        }

        // Itens que chegaram durante a gravação formam um novo lote (o seu _batchTotal foi
        // incrementado por Enqueue). Restaura o total deste lote antes de finalizá-lo.
        var pendingCount = _queue.Count;
        _batchTotal = completedTotal;
        IsBusy = false;

        // O ID do lote é capturado agora e viaja no evento: se outro lote começar antes
        // da verificação pós-atualização, o aviso ainda será gravado na entrada correta.
        var completedBatch = new BatchCompletedEventArgs(
            historyBatchId,
            completedUpgrades,
            completedGeneration);

        if (finalCanceled)
        {
            CompleteCanceledBatch(completedBatch);
        }
        else
        {
            CompleteBatch(completedBatch);
        }

        // Reinicia a fila que tenha chegado enquanto o histórico era gravado.
        if (pendingCount > 0)
        {
            ResetBatch();
            _batchTotal = pendingCount;
            StartProcessing();
        }
    }

    /// <summary>
    /// Finaliza um lote interrompido pelo usuário: mostra "Cancelado" na barra de atividade
    /// usando o brush de atenção âmbar (o mesmo dos avisos) e permite a auto-ocultação.
    /// </summary>
    private void CompleteCanceledBatch(BatchCompletedEventArgs completedBatch)
    {
        HasError = false;
        IsCanceledActivity = true;
        HasWarning = true;
        ActivityTitle = "Cancelado";
        ActivityDetail = "A operação foi cancelada pelo usuário.";
        IsActivityVisible = true;
        QueueCompleted?.Invoke(this, completedBatch);
    }

    private void CompleteBatch(BatchCompletedEventArgs completedBatch)
    {
        if (_batchFailed == 0 && _batchSkipped == 0)
        {
            HasError = false;
            ActivityTitle = _batchTotal == 1 ? "Tudo pronto!" : $"{_batchSucceeded} tarefas concluídas";
            ActivityDetail = _batchTotal == 1
                ? "A operação foi concluída com sucesso."
                : "As operações foram concluídas com sucesso.";
        }
        else if (_batchFailed == 0)
        {
            HasError = false;
            HasWarning = true;
            ActivityTitle = _batchSkipped == 1
                ? "Atualização ignorada"
                : $"{_batchSkipped} atualizações ignoradas";
            ActivityDetail = "Uma ou mais atualizações não puderam ser aplicadas porque o aplicativo " +
                             "continuou em uso. Feche-o ou permita o fechamento e tente novamente.";
        }
        else if (_batchSucceeded > 0 || _batchSkipped > 0)
        {
            HasError = true;
            ActivityTitle = "Algumas tarefas falharam";
            ActivityDetail = $"{_batchSucceeded} concluída(s) · {_batchFailed} com falha" +
                             (_batchSkipped > 0 ? $" · {_batchSkipped} ignorada(s)" : string.Empty) +
                             ". Abra os detalhes para ver o log.";
        }
        else
        {
            HasError = true;
            ActivityTitle = _batchTotal == 1 ? "Não foi possível concluir" : "As tarefas falharam";
            ActivityDetail = "Abra os detalhes para ver o que o winget informou.";
        }

        IsActivityVisible = true;
        QueueCompleted?.Invoke(this, completedBatch);
    }

    private async Task RestartServicesAsync(IReadOnlyList<string> services)
    {
        if (services.Count == 0)
        {
            return;
        }

        try
        {
            var result = await _runningApps.RestartServicesAsync(services);

            foreach (var service in result.RestartedServices)
            {
                AddLog($"Serviço reiniciado: {service}.");
            }

            foreach (var service in result.Failures)
            {
                AddLog($"Falha ao reiniciar o serviço: {service}.");
            }
        }
        catch (Exception exception)
        {
            AddLog($"Falha ao reiniciar serviços: {exception.Message}");
        }
    }

    private static string DescribeBlockers(RunningAppUsage usage)
    {
        var names = usage.Processes
            .Select(process => process.Name)
            .Concat(usage.Services.Select(service => $"serviço {service}"))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return string.Join(", ", names);
    }

    /// <summary>
    /// Publica o estado de um item da fila na UI thread (os await da fila podem retornar
    /// fora dela) para que as linhas correspondentes sejam atualizadas com segurança.
    /// </summary>
    private void RaiseItemState(
        PackageOperation operation,
        PackageOperationState state,
        string message,
        bool canceledBeforeStart = false)
    {
        void Raise()
        {
            ItemStateChanged?.Invoke(
                this,
                new OperationItemEventArgs(
                    operation.Id,
                    state,
                    operation.Kind,
                    message,
                    operation.InstalledVersion,
                    operation.Source,
                    canceledBeforeStart));
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

        // Parse da linha do winget: extrai % (determinado) e identifica a fase da operação.
        var lower = line.ToLowerInvariant();
        if (lower.Contains('%') && PercentRegex().Match(line) is { Success: true } match &&
            int.TryParse(match.Groups[1].Value, out var percent))
        {
            ProgressPercent = percent;
            CurrentPhase = OperationPhase.Downloading;
        }
        else if (lower.Contains("baixando") || lower.Contains("downloading"))
        {
            CurrentPhase = OperationPhase.Downloading;
            ProgressPercent = null;
        }
        else if (lower.Contains("instalando") || lower.Contains("installing"))
        {
            CurrentPhase = OperationPhase.Installing;
            ProgressPercent = null;
        }
        else if (lower.Contains("verificando") || lower.Contains("verifying"))
        {
            CurrentPhase = OperationPhase.Verifying;
            ProgressPercent = null;
        }
        else if (lower.Contains("concluído") || lower.Contains("completed"))
        {
            CurrentPhase = OperationPhase.None;
            ProgressPercent = null;
        }
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

    private static string BuildSourceSummary(IReadOnlyCollection<string> sources)
    {
        var distinct = sources
            .Select(source => source.Trim())
            .Where(source => source.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return distinct.Length switch
        {
            0 => string.Empty,
            1 => distinct[0],
            _ => string.Join(", ", distinct)
        };
    }

    [GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])")]
    private static partial Regex AnsiEscapeRegex();

    [GeneratedRegex(@"(\d{1,3})%")]
    private static partial Regex PercentRegex();
}

/// <summary>Fase da operação reportada pelo winget em cada linha de saída.</summary>
public enum OperationPhase
{
    None,
    Downloading,
    Installing,
    Verifying
}

/// <summary>Estado de um item dentro da fila do <see cref="OperationCenter"/>.</summary>
public enum PackageOperationState
{
    Queued,
    Running,
    Succeeded,
    Failed,

    /// <summary>Interrompido pelo usuário (cancelamento), não uma falha do winget.</summary>
    Canceled
}

/// <summary>
/// Argumentos de <see cref="OperationCenter.QueueCompleted"/>: o ID do lote no histórico
/// (nulo quando a gravação falhou), a relação de atualizações concluídas e a geração do
/// lote. Tudo é capturado no fim do lote para que os awaits da verificação pós-atualização
/// não confundam o lote concluído com um lote novo que tenha começado nesse intervalo.
/// </summary>
public sealed class BatchCompletedEventArgs : EventArgs
{
    public BatchCompletedEventArgs(
        string? historyBatchId,
        IReadOnlyList<PackageOperation> completedUpgrades,
        long batchGeneration)
    {
        HistoryBatchId = historyBatchId;
        CompletedUpgrades = completedUpgrades;
        BatchGeneration = batchGeneration;
    }

    /// <summary>ID da entrada de histórico gravada, ou nulo se a gravação falhou.</summary>
    public string? HistoryBatchId { get; }

    /// <summary>Atualizações que o winget reportou como bem-sucedidas neste lote.</summary>
    public IReadOnlyList<PackageOperation> CompletedUpgrades { get; }

    /// <summary>
    /// Geração do lote concluído. Comparada com <see cref="OperationCenter.TryReportStaleUpgrades"/>
    /// para impedir que um evento antigo sobrescreva a atividade de um lote mais novo.
    /// </summary>
    public long BatchGeneration { get; }
}

/// <summary>
/// Argumentos de <see cref="OperationCenter.ItemStateChanged"/>: identifica o pacote pelo Id
/// e, nos upgrades, também pela versão instalada e origem. O Id sozinho não distingue
/// versões lado a lado do mesmo pacote (ex.: .NET SDK), então a identidade completa evita
/// pintar a linha errada.
/// </summary>
public sealed class OperationItemEventArgs : EventArgs
{
    public OperationItemEventArgs(
        string packageId,
        PackageOperationState state,
        PackageOperationKind kind,
        string message,
        string installedVersion,
        string source,
        bool canceledBeforeStart = false)
    {
        PackageId = packageId;
        State = state;
        Kind = kind;
        Message = message;
        InstalledVersion = installedVersion ?? string.Empty;
        Source = source ?? string.Empty;
        CanceledBeforeStart = canceledBeforeStart;
    }

    /// <summary>Id do pacote (winget), usado para casar com as linhas exibidas.</summary>
    public string PackageId { get; }

    public PackageOperationState State { get; }

    /// <summary>Instalação ou atualização: define o texto "Instalando…" vs "Atualizando…".</summary>
    public PackageOperationKind Kind { get; }

    /// <summary>Mensagem de falha (vazia nos demais estados).</summary>
    public string Message { get; }

    /// <summary>
    /// Versão instalada que originou o upgrade (vazia em instalações). Combina com
    /// <see cref="PackageId"/> e <see cref="Source"/> para achar a linha exata.
    /// </summary>
    public string InstalledVersion { get; }

    /// <summary>
    /// Origem do alvo (ex.: winget, msstore) herdada da operação enfileirada, usada no
    /// matching exato da linha. Vazia significa "desconhecida" e nunca funciona como
    /// curinga; nesse caso nenhuma linha é escolhida por suposição.
    /// </summary>
    public string Source { get; }

    /// <summary>
    /// True quando o item foi cancelado ainda na fila (nunca começou): a linha correspondente
    /// volta direto para Idle em vez de exibir "Cancelado".
    /// </summary>
    public bool CanceledBeforeStart { get; }
}
