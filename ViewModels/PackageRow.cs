using Asa.Helpers;
using Asa.Models;
using Microsoft.UI.Dispatching;

namespace Asa.ViewModels;

/// <summary>
/// Estado da operação refletido na coluna de ação da linha. É o que o
/// <see cref="PackagesViewModel"/> aplica a partir das notificações do OperationCenter.
/// </summary>
public enum PackageRowState
{
    Idle,
    Queued,
    Running,
    Succeeded,
    Failed,

    /// <summary>Operação interrompida pelo usuário (não é falha do winget).</summary>
    Canceled
}

public sealed class PackageRow : ObservableObject
{
    /// <summary>
    /// Tempo mínimo em que "Na fila" fica visível. A fila inicia o processamento no mesmo
    /// ciclo de UI do enfileiramento; sem isso o estado Queued seria pintado e substituído
    /// por Running antes de qualquer quadro aparecer. Só retarda a PINTURA, não a fila.
    /// </summary>
    private static readonly TimeSpan MinimumQueuedDisplay = TimeSpan.FromMilliseconds(400);

    /// <summary>Tempo em que "Concluído"/"Cancelado" fica visível antes de voltar para Idle.</summary>
    private static readonly TimeSpan TransientStateDisplay = TimeSpan.FromSeconds(3);

    private readonly Action<PackageRow> _requestUpdate;
    private bool _isSelected;
    private bool _isQueued;
    private PackageRowState _state = PackageRowState.Idle;
    private string _stateMessage = string.Empty;
    private bool _isUpgradeOperation = true;
    private DispatcherQueue? _dispatcherQueue;
    private int _stateVersion;
    private DateTimeOffset _queuedSince;

    public PackageRow(InstalledPackage package, Action<PackageRow> requestUpdate)
    {
        Package = package;
        _requestUpdate = requestUpdate;
        UpdateCommand = new RelayCommand(
            _ => _requestUpdate(this),
            _ => CanUpdate && !IsQueued);
    }

    public InstalledPackage Package { get; private set; }

    /// <summary>
    /// Substitui o pacote subjacente reaproveitando a mesma linha: só dispara as propriedades
    /// que realmente mudaram, preservando seleção/estado de fila e evitando repintar a lista.
    /// </summary>
    public void UpdatePackage(InstalledPackage package)
    {
        if (ReferenceEquals(Package, package))
        {
            return;
        }

        var previous = Package;
        Package = package;

        if (!string.Equals(previous.Name, package.Name, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Initial));
            OnPropertyChanged(nameof(RowStatusAutomationName));
        }

        if (!string.Equals(previous.Id, package.Id, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(Id));
        }

        if (!string.Equals(previous.CurrentVersionDisplay, package.CurrentVersionDisplay, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(CurrentVersion));
        }

        if (!string.Equals(previous.CurrentVersionCaption, package.CurrentVersionCaption, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(CurrentVersionCaption));
        }

        if (!string.Equals(previous.CurrentVersionToolTip, package.CurrentVersionToolTip, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(CurrentVersionToolTip));
        }

        if (previous.HasDetectedVersion != package.HasDetectedVersion)
        {
            OnPropertyChanged(nameof(HasDetectedVersion));
        }

        if (!string.Equals(previous.AvailableVersion, package.AvailableVersion, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(AvailableVersion));
        }

        if (!string.Equals(previous.Source, package.Source, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(SourceLabel));
        }

        if (previous.HasUpdate != package.HasUpdate)
        {
            OnPropertyChanged(nameof(HasUpdate));
        }

        if (previous.CanUpdate != package.CanUpdate)
        {
            OnPropertyChanged(nameof(CanUpdate));
            OnPropertyChanged(nameof(ShowInSync));
            OnPropertyChanged(nameof(ShowActionButton));
            UpdateCommand.NotifyCanExecuteChanged();
        }
    }

    public string Name => Package.Name;

    public string Id => Package.Id;

    public string CurrentVersion => Package.CurrentVersionDisplay;

    /// <summary>Legenda da versão instalada ("instalada"); a verificação local é sinalizada pelo indicador de escudo.</summary>
    public string CurrentVersionCaption => Package.CurrentVersionCaption;

    /// <summary>Texto explicativo da versão apresentada (versão + origem).</summary>
    public string CurrentVersionToolTip => Package.CurrentVersionToolTip;

    /// <summary>Indica que a versão foi detectada localmente.</summary>
    public bool HasDetectedVersion => Package.HasDetectedVersion;

    public string AvailableVersion => Package.AvailableVersionDisplay;

    public string SourceLabel => Package.SourceDisplay;

    public string Initial => Package.Initial;

    public bool HasUpdate => Package.HasUpdate;

    public bool CanUpdate => Package.CanUpdate;

    public bool ShowInSync => !CanUpdate;

    public RelayCommand UpdateCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsQueued
    {
        get => _isQueued;
        private set
        {
            if (SetProperty(ref _isQueued, value))
            {
                UpdateCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(ActionToolTip));
            }
        }
    }

    public string ActionToolTip => IsQueued ? "Na fila" : "Atualizar agora";

    public void MarkQueued()
    {
        IsQueued = true;
        IsSelected = false;

        // Rede de segurança: o OperationCenter já notifica o estado (evento ItemStateChanged)
        // e nesta chamada a linha normalmente já está Queued; só age se nada notificou.
        if (State == PackageRowState.Idle)
        {
            ApplyOperationState(PackageRowState.Queued, isUpgradeOperation: true);
        }
    }

    /// <summary>Estado atual da operação desta linha (Idle quando nenhuma operação está ativa).</summary>
    public PackageRowState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                if (value == PackageRowState.Queued)
                {
                    _queuedSince = DateTimeOffset.UtcNow;
                }

                NotifyStateDependentProperties();
            }
        }
    }

    /// <summary>Mensagem de falha exibida como tooltip do glifo de erro.</summary>
    public string StateMessage
    {
        get => _stateMessage;
        private set => SetProperty(ref _stateMessage, value);
    }

    /// <summary>Está enfileirado ou em execução (fila ativa para esta linha).</summary>
    public bool IsBusyRow => State is PackageRowState.Queued or PackageRowState.Running;

    /// <summary>Texto exibido no indicador da coluna de ação.</summary>
    public string StateText => State switch
    {
        PackageRowState.Queued => "Na fila",
        PackageRowState.Running => _isUpgradeOperation ? "Atualizando…" : "Instalando…",
        PackageRowState.Succeeded => "Concluído",
        PackageRowState.Failed => "Falhou",
        PackageRowState.Canceled => "Cancelado",
        _ => string.Empty
    };

    /// <summary>Nome de acessibilidade do indicador, ex.: "7-Zip: instalando".</summary>
    public string RowStatusAutomationName =>
        State == PackageRowState.Idle ? Name : $"{Name}: {StateAutomationText}";

    private string StateAutomationText => State switch
    {
        PackageRowState.Queued => "na fila",
        PackageRowState.Running => _isUpgradeOperation ? "atualizando" : "instalando",
        PackageRowState.Succeeded => "concluído",
        PackageRowState.Failed => "falhou",
        PackageRowState.Canceled => "cancelado",
        _ => string.Empty
    };

    // Apenas um destes fica visível por vez, todos dentro da MESMA célula do botão de ação:
    // a coluna tem largura fixa e a linha nunca muda de altura (zero layout shift).

    public bool ShowActionButton => CanUpdate && State == PackageRowState.Idle;

    public bool ShowQueueIndicator => State == PackageRowState.Queued;

    public bool ShowRunningIndicator => State == PackageRowState.Running;

    public bool ShowSucceededIndicator => State == PackageRowState.Succeeded;

    public bool ShowFailedIndicator => State == PackageRowState.Failed;

    public bool ShowCanceledIndicator => State == PackageRowState.Canceled;

    /// <summary>
    /// Aplica um estado reportado pelo <c>OperationCenter.ItemStateChanged</c>
    /// (sempre chamado na UI thread).
    /// </summary>
    public void ApplyOperationState(PackageRowState state, bool isUpgradeOperation, string message = "")
    {
        _dispatcherQueue ??= DispatcherQueue.GetForCurrentThread();

        var version = ++_stateVersion;

        if (state == PackageRowState.Running && State == PackageRowState.Queued)
        {
            var remaining = MinimumQueuedDisplay - (DateTimeOffset.UtcNow - _queuedSince);
            if (remaining > TimeSpan.Zero && _dispatcherQueue is not null)
            {
                _ = ApplyStateAfterAsync(version, remaining, state, isUpgradeOperation, message);
                return;
            }
        }

        ApplyStateCore(version, state, isUpgradeOperation, message);
    }

    private async Task ApplyStateAfterAsync(
        int version,
        TimeSpan delay,
        PackageRowState state,
        bool isUpgradeOperation,
        string message)
    {
        await Task.Delay(delay);

        if (version == _stateVersion)
        {
            ApplyStateCore(version, state, isUpgradeOperation, message);
        }
    }

    private void ApplyStateCore(int version, PackageRowState state, bool isUpgradeOperation, string message)
    {
        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => ApplyStateCore(version, state, isUpgradeOperation, message));
            return;
        }

        if (version != _stateVersion)
        {
            // Um estado mais novo chegou enquanto este esperava: o mais novo sempre vence.
            return;
        }

        if (_isUpgradeOperation != isUpgradeOperation)
        {
            _isUpgradeOperation = isUpgradeOperation;
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(RowStatusAutomationName));
        }

        StateMessage = message;
        State = state;

        // Enquanto a linha está em execução ela segue "ocupada": o botão de ação fica
        // oculto pelo indicador e o comando continua desabilitado (IsQueued => CanExecute false).
        IsQueued = state is PackageRowState.Queued or PackageRowState.Running;

        if (state is PackageRowState.Succeeded or PackageRowState.Canceled)
        {
            // "Concluído"/"Cancelado" ficam ~3 s visíveis e a linha volta para Idle
            // (o botão de ação reaparece, permitindo nova tentativa imediata).
            _ = ReturnToIdleAfterAsync(version);
        }
    }

    private async Task ReturnToIdleAfterAsync(int version)
    {
        await Task.Delay(TransientStateDisplay);

        if (version != _stateVersion || !IsTransientResultState(State))
        {
            return;
        }

        void Reset()
        {
            if (version != _stateVersion || !IsTransientResultState(State))
            {
                return;
            }

            State = PackageRowState.Idle;
            IsQueued = false;
        }

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(Reset);
        }
        else
        {
            Reset();
        }
    }

    private static bool IsTransientResultState(PackageRowState state)
    {
        return state is PackageRowState.Succeeded or PackageRowState.Canceled;
    }

    private void NotifyStateDependentProperties()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsBusyRow));
        OnPropertyChanged(nameof(RowStatusAutomationName));
        OnPropertyChanged(nameof(ShowActionButton));
        OnPropertyChanged(nameof(ShowQueueIndicator));
        OnPropertyChanged(nameof(ShowRunningIndicator));
        OnPropertyChanged(nameof(ShowSucceededIndicator));
        OnPropertyChanged(nameof(ShowFailedIndicator));
        OnPropertyChanged(nameof(ShowCanceledIndicator));
        UpdateCommand.NotifyCanExecuteChanged();
    }
}
