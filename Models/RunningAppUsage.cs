namespace Asa.Models;

public enum RunningAppProcessKind
{
    Application,
    Explorer,
    Critical
}

public sealed record RunningAppProcessInfo(
    int ProcessId,
    string Name,
    RunningAppProcessKind Kind);

public sealed class RunningAppUsage
{
    public required string PackageName { get; init; }

    public required string PackageId { get; init; }

    public required string InstallPath { get; init; }

    public required IReadOnlyList<RunningAppProcessInfo> Processes { get; init; }

    public required IReadOnlyList<string> Services { get; init; }

    public bool HasBlockers => Processes.Count > 0 || Services.Count > 0;

    public bool HasCriticalProcesses => Processes.Any(process => process.Kind == RunningAppProcessKind.Critical);

    public bool HasExplorer => Processes.Any(process => process.Kind == RunningAppProcessKind.Explorer);

    public IReadOnlyList<RunningAppProcessInfo> ClosableProcesses => Processes
        .Where(process => process.Kind != RunningAppProcessKind.Critical)
        .GroupBy(process => process.ProcessId)
        .Select(group => group.First())
        .ToList();
}

public sealed record RunningAppCloseResult(
    bool Success,
    bool ExplorerClosed,
    IReadOnlyList<string> StoppedServices,
    IReadOnlyList<string> Failures);

public sealed record RunningAppRestartResult(
    IReadOnlyList<string> RestartedServices,
    IReadOnlyList<string> Failures);
