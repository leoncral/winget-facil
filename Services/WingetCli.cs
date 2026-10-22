using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Asa.Models;

namespace Asa.Services;

public sealed record WingetCommandResult(
    int ExitCode,
    string Output,
    string Error,
    bool WasCanceled,
    string CommandLine)
{
    public bool IsSuccess => !WasCanceled && ExitCode == 0;

    public string ExitCodeDisplay => ExitCode == 0
        ? "0"
        : $"0x{unchecked((uint)ExitCode):X8}";
}

public sealed class WingetCliException : Exception
{
    public WingetCliException(string message)
        : base(message)
    {
    }
}

public sealed partial class WingetCli
{
    private const int MaxSearchResults = 40;

    /// <summary>Espera máxima após um Kill antes de desistir (não prende o chamador para sempre).</summary>
    private static readonly TimeSpan ProcessKillTimeout = TimeSpan.FromSeconds(5);

    private readonly string? _executablePath;
    private readonly object _processGate = new();
    private Process? _currentProcess;

    public WingetCli()
    {
        _executablePath = ResolveExecutable();
    }

    public string? ExecutablePath => _executablePath;

    public bool IsAvailable => _executablePath is not null;

    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return null;
        }

        var result = await RunAsync(["--version"], null, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return null;
        }

        return FirstMeaningfulLine(result.Output);
    }

    public async Task<IReadOnlyList<InstalledPackage>> GetInstalledPackagesAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();

        WingetCommandResult result;
        for (var attempt = 0; ; attempt++)
        {
            result = await RunAsync(
                ["list", "--count", "1000", "--accept-source-agreements", "--disable-interactivity", "--nowarn"],
                null,
                cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                break;
            }

            if (attempt >= 2 || !IsTransientLaunchFailure(result) || cancellationToken.IsCancellationRequested)
            {
                throw new WingetCliException(DescribeFailure("consultar os programas instalados", result));
            }

            // The winget executable is briefly locked while App Installer updates itself.
            await Task.Delay(TimeSpan.FromMilliseconds(700 * (attempt + 1)), cancellationToken)
                .ConfigureAwait(false);
        }

        var rows = WingetTableParser.Parse(result.Output);
        var packages = new List<InstalledPackage>(rows.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var key = $"{row.Id}\u001f{row.Version}\u001f{row.AvailableVersion}\u001f{row.Source}";
            if (!seen.Add(key))
            {
                continue;
            }

            packages.Add(new InstalledPackage
            {
                Name = row.Name,
                Id = row.Id,
                Version = row.Version,
                AvailableVersion = row.AvailableVersion,
                Source = row.Source
            });
        }

        return packages
            .OrderBy(package => package.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<SearchPackage>> SearchPackagesAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();

        var trimmedQuery = query.Trim();
        if (trimmedQuery.Length == 0)
        {
            return [];
        }

        var result = await RunAsync(
            [
                "search",
                "--query",
                trimmedQuery,
                "--count",
                MaxSearchResults.ToString(),
                "--accept-source-agreements",
                "--disable-interactivity",
                "--nowarn"
            ],
            null,
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            if (LooksLikeNoResults(result))
            {
                return [];
            }

            throw new WingetCliException(DescribeFailure("buscar no catálogo do winget", result));
        }

        var rows = WingetTableParser.Parse(result.Output);
        var packages = new List<SearchPackage>(rows.Count);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            if (!seenIds.Add(row.Id))
            {
                continue;
            }

            packages.Add(new SearchPackage(
                row.Name,
                row.Id,
                row.Version,
                string.IsNullOrWhiteSpace(row.Source) ? "winget" : row.Source));
        }

        return packages;
    }

    public Task<WingetCommandResult> InstallAsync(
        PackageOperation operation,
        bool silent,
        Action<string>? onOutput,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();

        var arguments = new List<string>
        {
            "install",
            "--id",
            operation.Id,
            "--exact",
            "--accept-package-agreements",
            "--accept-source-agreements",
            "--disable-interactivity"
        };

        AddSource(arguments, operation.Source);
        if (silent)
        {
            arguments.Add("--silent");
        }

        return RunAsync(arguments, onOutput, cancellationToken);
    }

    public Task<WingetCommandResult> UpgradeAsync(
        PackageOperation operation,
        bool silent,
        Action<string>? onOutput,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();

        var arguments = new List<string>
        {
            "upgrade",
            "--id",
            operation.Id,
            "--exact",
            "--include-unknown",
            "--accept-package-agreements",
            "--accept-source-agreements",
            "--disable-interactivity"
        };

        AddSource(arguments, operation.Source);
        if (silent)
        {
            arguments.Add("--silent");
        }

        return RunAsync(arguments, onOutput, cancellationToken);
    }

    public Task<WingetCommandResult> ExecuteOperationAsync(
        PackageOperation operation,
        bool silent,
        Action<string>? onOutput,
        CancellationToken cancellationToken = default)
    {
        return operation.Kind == PackageOperationKind.Install
            ? InstallAsync(operation, silent, onOutput, cancellationToken)
            : UpgradeAsync(operation, silent, onOutput, cancellationToken);
    }

    /// <summary>
    /// Executa <c>winget show --id &lt;id&gt; --exact</c> com as opções padrão de
    /// não-interatividade. Útil para obter metadados sob demanda (ex.: tamanho do
    /// download) sem lançar exceções quando o pacote não é encontrado.
    /// </summary>
    /// <param name="source">Origem opcional (ex.: "winget", "msstore"). Quando informado,
    /// é passado como <c>--source</c> ao winget, evitando que a consulta devolva dados de
    /// outra versão/origem.</param>
    /// <param name="version">Versão opcional do pacote. Quando informada, é passada como
    /// <c>--version</c> ao winget, fazendo com que a consulta devolva metadados dessa
    /// versão específica.</param>
    public Task<WingetCommandResult> ShowAsync(
        string packageId,
        string? source = null,
        string? version = null,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>
        {
            "show",
            "--id",
            packageId,
            "--exact",
            "--accept-source-agreements",
            "--disable-interactivity",
            "--nowarn"
        };

        if (!string.IsNullOrWhiteSpace(source))
        {
            arguments.Add("--source");
            arguments.Add(source);
        }

        if (!string.IsNullOrWhiteSpace(version))
        {
            arguments.Add("--version");
            arguments.Add(version);
        }

        return RunAsync(arguments, null, cancellationToken);
    }

    /// <summary>
    /// Encerra imediatamente o winget em execução lançado por esta instância, se houver,
    /// incluindo a árvore de processos filhos (instaladores iniciados por ele). A espera
    /// após o kill é limitada para não prender o chamador. Retorna false quando não há
    /// processo a encerrar ou quando ele já havia finalizado.
    /// </summary>
    public async Task<bool> KillCurrentProcessAsync()
    {
        Process? process;
        lock (_processGate)
        {
            process = _currentProcess;
        }

        if (process is null)
        {
            return false;
        }

        return await KillProcessTreeAsync(process).ConfigureAwait(false);
    }

    /// <summary>
    /// Encerra o processo (e os filhos) com tratamento para "já finalizado" e espera limitada.
    /// </summary>
    private static async Task<bool> KillProcessTreeAsync(Process process)
    {
        var killed = false;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                killed = true;
            }
        }
        catch (InvalidOperationException)
        {
            // O processo finalizou exatamente agora.
        }
        catch (Win32Exception)
        {
            // Sem permissão para encerrar; a espera abaixo ainda respeita o limite.
        }

        try
        {
            // Espera limitada: se o kill falhar (ex.: acesso negado), o chamador não fica preso.
            await process.WaitForExitAsync().WaitAsync(ProcessKillTimeout).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or InvalidOperationException or ObjectDisposedException)
        {
            // Timeout ou processo já removido: seguimos sem ele.
        }

        return killed;
    }

    public static string DescribeFailure(string action, WingetCommandResult result)
    {
        if (result.WasCanceled)
        {
            return "A operação foi cancelada.";
        }

        if (result.ExitCode == -1)
        {
            return $"Não foi possível {action}. O winget não pôde ser iniciado agora. Tente novamente em alguns segundos.";
        }

        var detail = FirstMeaningfulLine(result.Error);
        if (string.IsNullOrWhiteSpace(detail))
        {
            detail = FirstMeaningfulLine(result.Output);
        }

        if (!string.IsNullOrWhiteSpace(detail) &&
            !detail.StartsWith("Windows Package Manager", StringComparison.OrdinalIgnoreCase))
        {
            return $"Não foi possível {action}. O winget informou: {detail}";
        }

        return $"Não foi possível {action}. O winget retornou o código {result.ExitCodeDisplay}.";
    }

    private static void AddSource(ICollection<string> arguments, string source)
    {
        if (string.IsNullOrWhiteSpace(source) ||
            string.Equals(source, "local", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        arguments.Add("--source");
        arguments.Add(source);
    }

    private static bool IsTransientLaunchFailure(WingetCommandResult result)
    {
        return !result.WasCanceled && result.ExitCode == -1 && !string.IsNullOrWhiteSpace(result.Error);
    }

    private static bool LooksLikeNoResults(WingetCommandResult result)
    {
        var text = $"{result.Output}\n{result.Error}";
        return text.Contains("No package found", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Nenhum pacote", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("No installed package found", StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            throw new WingetCliException(
                "O winget não foi encontrado neste computador. Instale o App Installer da Microsoft e tente novamente.");
        }
    }

    private async Task<WingetCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        Action<string>? onOutput,
        CancellationToken cancellationToken)
    {
        var commandLine = "winget " + string.Join(' ', arguments.Select(QuoteArgument));

        if (_executablePath is null)
        {
            return new WingetCommandResult(
                -1,
                string.Empty,
                "O winget não foi encontrado.",
                false,
                commandLine);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["NO_COLOR"] = "1";

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        var error = new StringBuilder();

        try
        {
            if (!process.Start())
            {
                return new WingetCommandResult(-1, string.Empty, "Não foi possível iniciar o winget.", false, commandLine);
            }
        }
        catch (Exception exception)
        {
            return new WingetCommandResult(-1, string.Empty, exception.Message, false, commandLine);
        }

        var outputTask = PumpAsync(process.StandardOutput, output, onOutput, cancellationToken);
        var errorTask = PumpAsync(process.StandardError, error, null, cancellationToken);
        var wasCanceled = false;

        // Torna o processo alcançável para CancelCurrentAsync (cancelar a operação em andamento).
        lock (_processGate)
        {
            _currentProcess = process;
        }

        try
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                wasCanceled = true;
                await KillProcessTreeAsync(process).ConfigureAwait(false);
            }

            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);

            var exitCode = wasCanceled ? -1 : process.ExitCode;
            return new WingetCommandResult(exitCode, output.ToString(), error.ToString(), wasCanceled, commandLine);
        }
        finally
        {
            lock (_processGate)
            {
                if (ReferenceEquals(_currentProcess, process))
                {
                    _currentProcess = null;
                }
            }
        }
    }

    private static async Task PumpAsync(
        StreamReader reader,
        StringBuilder buffer,
        Action<string>? onOutput,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (line is null)
                {
                    break;
                }

                buffer.AppendLine(line);
                onOutput?.Invoke(line);
            }
        }
        catch (ObjectDisposedException)
        {
            // The process stream was disposed after the process exited.
        }
        catch (IOException)
        {
            // The process can close its pipes at any moment.
        }
    }

    private static string FirstMeaningfulLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = AnsiEscapeRegex().Replace(rawLine, string.Empty)
                .Replace("\r", string.Empty, StringComparison.Ordinal)
                .Trim();

            if (line.Length > 0 &&
                !line.All(character => character is '-' or '=' or ' '))
            {
                return line.Length > 240 ? line[..240] + "…" : line;
            }
        }

        return string.Empty;
    }

    private static string QuoteArgument(string argument)
    {
        return argument.Contains(' ')
            ? $"\"{argument}\""
            : argument;
    }

    private static string? ResolveExecutable()
    {
        var localAlias = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps",
            "winget.exe");

        if (File.Exists(localAlias))
        {
            return localAlias;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(folder.Trim(), "winget.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // Ignore malformed PATH entries.
                }
            }
        }

        return null;
    }

    [GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])")]
    private static partial Regex AnsiEscapeRegex();
}
