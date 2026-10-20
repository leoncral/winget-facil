using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text;
using Asa.Models;
using Asa.Services.Native;
using Microsoft.Win32;
using Windows.Management.Deployment;

namespace Asa.Services;

/// <summary>
/// Finds running processes and services that would block an MSIX/EXE update.
/// The association is made from the package installation folder, never from a
/// hard-coded list of process names.
/// </summary>
public sealed class RunningAppGuard
{
    private const int MaxRegisteredFiles = 6000;

    private static readonly string[] BlockedDirectories =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
    ];

    private static readonly HashSet<string> GenericTokens = new(StringComparer.Ordinal)
    {
        "microsoft", "windows", "visual", "redistributable", "runtime", "update",
        "driver", "drivers", "service", "services", "client", "server", "tools",
        "tool", "sdk", "setup", "installer", "installation", "corporation", "corp",
        "software", "systems", "system", "technologies", "solutions", "network",
        "support", "manager", "console", "edition", "version", "desktop", "pro",
        "professional", "plus", "scan", "print", "printers", "viewer", "view",
        "reader", "media", "photo", "video", "audio", "music", "file", "files",
        "data", "cloud", "drive", "office", "home", "browser", "standard",
        "community", "ultimate", "essential", "essentials", "suite", "component",
        "components", "library", "libraries"
    };

    public Task<RunningAppUsage?> DetectAsync(
        string packageName,
        string packageId,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => DetectCore(packageName, packageId, cancellationToken), cancellationToken);
    }

    public Task<RunningAppCloseResult> CloseAsync(
        RunningAppUsage usage,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => CloseCore(usage, cancellationToken), cancellationToken);
    }

    public Task<RunningAppRestartResult> RestartServicesAsync(
        IReadOnlyList<string> services,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var restarted = new List<string>();
            var failures = new List<string>();

            foreach (var service in services.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (WindowsServiceControl.Start(service, TimeSpan.FromSeconds(15)))
                {
                    restarted.Add(service);
                }
                else
                {
                    failures.Add(service);
                }
            }

            return new RunningAppRestartResult(restarted, failures);
        }, cancellationToken);
    }

    public void EnsureExplorerRunning()
    {
        try
        {
            if (Process.GetProcessesByName("explorer").Length > 0)
            {
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // The shell can be restarted manually by the user if needed.
        }
    }

    private static RunningAppUsage? DetectCore(
        string packageName,
        string packageId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var installPath = FindInstallPath(packageName, packageId);
        var processes = new List<RunningAppProcessInfo>();
        var services = new List<string>();

        if (installPath is not null)
        {
            foreach (var service in FindServicesInFolder(installPath))
            {
                AddUnique(services, service);
            }

            var files = CollectPackageFiles(installPath);
            if (files.Count > 0)
            {
                foreach (var item in RestartManager.FindProcesses(files))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (item.ProcessId == Environment.ProcessId)
                    {
                        continue;
                    }

                    if (item.Type == RestartManager.RestartManagerAppType.Service)
                    {
                        if (!string.IsNullOrWhiteSpace(item.ServiceShortName))
                        {
                            AddUnique(services, item.ServiceShortName);
                        }

                        continue;
                    }

                    var processName = ResolveProcessName(item);
                    var kind = item.Type switch
                    {
                        RestartManager.RestartManagerAppType.Explorer => RunningAppProcessKind.Explorer,
                        RestartManager.RestartManagerAppType.Critical => RunningAppProcessKind.Critical,
                        _ when IsExplorer(processName) => RunningAppProcessKind.Explorer,
                        _ => RunningAppProcessKind.Application
                    };

                    processes.Add(new RunningAppProcessInfo(item.ProcessId, processName, kind));
                }
            }
        }

        // Some installers (NSIS, Inno, MSI without ARP location) do not expose the
        // installation folder. In that case, match running processes against the
        // package name itself, then let the user confirm before anything is closed.
        foreach (var candidate in FindProcessesByNameTokens(packageName, packageId))
        {
            if (processes.All(process => process.ProcessId != candidate.ProcessId))
            {
                processes.Add(candidate);
            }
        }

        processes = processes
            .GroupBy(process => process.ProcessId)
            .Select(group => group.First())
            .ToList();

        if (processes.Count == 0 && services.Count == 0)
        {
            return null;
        }

        return new RunningAppUsage
        {
            PackageName = packageName,
            PackageId = packageId,
            InstallPath = installPath ?? string.Empty,
            Processes = processes,
            Services = services
        };
    }

    private static IReadOnlyList<RunningAppProcessInfo> FindProcessesByNameTokens(
        string packageName,
        string packageId)
    {
        var tokens = BuildSearchTokens(packageName, packageId);
        if (tokens.Count == 0)
        {
            return [];
        }

        var fullName = NormalizeName(packageName);
        var currentSession = -1;

        try
        {
            using var current = Process.GetCurrentProcess();
            currentSession = current.SessionId;
        }
        catch
        {
            // Session filtering is skipped when the current process cannot be queried.
        }

        var result = new List<RunningAppProcessInfo>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId)
                    {
                        continue;
                    }

                    if (currentSession >= 0 && process.SessionId != currentSession)
                    {
                        continue;
                    }

                    var processName = NormalizeName(process.ProcessName);
                    if (processName.Length < 3)
                    {
                        continue;
                    }

                    string? path = null;
                    try
                    {
                        path = process.MainModule?.FileName;
                    }
                    catch
                    {
                        // Protected or already exiting processes have no readable module.
                    }

                    var matched = false;

                    if (fullName.Length >= 5 &&
                        processName.Contains(fullName, StringComparison.Ordinal))
                    {
                        matched = true;
                    }

                    if (!matched)
                    {
                        foreach (var token in tokens)
                        {
                            if (processName.StartsWith(token, StringComparison.Ordinal) ||
                                (token.Length >= 6 &&
                                 processName.Contains(token, StringComparison.Ordinal)))
                            {
                                matched = true;
                                break;
                            }
                        }
                    }

                    if (!matched && path is not null)
                    {
                        matched = PathMatchesTokens(path, tokens, fullName);
                    }

                    if (matched)
                    {
                        result.Add(new RunningAppProcessInfo(
                            process.Id,
                            process.ProcessName,
                            RunningAppProcessKind.Application));
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process exited while it was being inspected.
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Access denied for protected processes; they are not user apps.
                }
                catch (NotSupportedException)
                {
                    // Some remote or unusual processes do not expose these properties.
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<string> BuildSearchTokens(string packageName, string packageId)
    {
        var tokens = new List<string>();

        void AddFrom(string value)
        {
            var parts = value.Split(
                [' ', '.', ',', '-', '_', '(', ')', '[', ']', '\'', '"', '/', '\\', '+', ':', ';', '\t'],
                StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                var normalized = NormalizeName(part);
                if (normalized.Length >= 4 &&
                    !normalized.All(char.IsDigit) &&
                    !GenericTokens.Contains(normalized) &&
                    !tokens.Contains(normalized))
                {
                    tokens.Add(normalized);
                }
            }
        }

        AddFrom(packageName);
        AddFrom(packageId);

        return tokens;
    }

    private static bool PathMatchesTokens(
        string path,
        IReadOnlyList<string> tokens,
        string fullName)
    {
        if (IsSystemPath(path))
        {
            return false;
        }

        foreach (var segment in path.Split('\\', '/'))
        {
            var normalized = NormalizeName(segment);
            if (normalized.Length < 3)
            {
                continue;
            }

            if (fullName.Length >= 5 &&
                normalized.Contains(fullName, StringComparison.Ordinal))
            {
                return true;
            }

            foreach (var token in tokens)
            {
                if (token.Length >= 5 &&
                    normalized.Contains(token, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsSystemPath(string path)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windows))
        {
            return false;
        }

        var prefix = windows.TrimEnd('\\') + '\\';
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static RunningAppCloseResult CloseCore(
        RunningAppUsage usage,
        CancellationToken cancellationToken)
    {
        var stoppedServices = new List<string>();
        var failures = new List<string>();
        var explorerClosed = false;

        foreach (var process in usage.Processes.Where(process => process.Kind == RunningAppProcessKind.Critical))
        {
            failures.Add($"{process.Name} (processo crítico do Windows)");
        }

        foreach (var service in usage.Services)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!WindowsServiceControl.IsRunning(service))
            {
                continue;
            }

            if (WindowsServiceControl.Stop(service, TimeSpan.FromSeconds(15)))
            {
                AddUnique(stoppedServices, service);
            }
            else
            {
                failures.Add($"serviço {service}");
            }
        }

        foreach (var process in usage.ClosableProcesses)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.Kind == RunningAppProcessKind.Explorer)
            {
                explorerClosed = true;
            }

            if (!CloseProcess(process))
            {
                failures.Add($"{process.Name} (PID {process.ProcessId})");
            }
        }

        if (failures.Count > 0)
        {
            return new RunningAppCloseResult(false, explorerClosed, stoppedServices, failures);
        }

        // Confirm that nothing reopened or is still shutting down before calling winget.
        RunningAppUsage? remaining = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Thread.Sleep(500);
            try
            {
                remaining = DetectCore(usage.PackageName, usage.PackageId, CancellationToken.None);
            }
            catch
            {
                remaining = null;
                break;
            }

            if (remaining is null)
            {
                break;
            }
        }

        if (remaining is not null)
        {
            var remainingNames = remaining.Processes
                .Select(process => process.Name)
                .Concat(remaining.Services)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            failures.Add($"ainda em execução: {string.Join(", ", remainingNames)}");
            return new RunningAppCloseResult(false, explorerClosed, stoppedServices, failures);
        }

        return new RunningAppCloseResult(true, explorerClosed, stoppedServices, failures);
    }

    private static bool CloseProcess(RunningAppProcessInfo info)
    {
        try
        {
            using var process = Process.GetProcessById(info.ProcessId);
            if (process.HasExited || process.Id == Environment.ProcessId)
            {
                return true;
            }

            if (info.Kind != RunningAppProcessKind.Explorer)
            {
                try
                {
                    process.CloseMainWindow();
                }
                catch
                {
                    // Some processes do not expose a main window.
                }

                if (process.WaitForExit(3500))
                {
                    return true;
                }
            }

            try
            {
                process.Kill(entireProcessTree: info.Kind != RunningAppProcessKind.Explorer);
            }
            catch
            {
                // Access can be denied for protected processes.
            }

            return process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveProcessName(RestartManager.RestartManagerProcess item)
    {
        if (!string.IsNullOrWhiteSpace(item.ApplicationName))
        {
            return item.ApplicationName.Trim();
        }

        try
        {
            using var process = Process.GetProcessById(item.ProcessId);
            return $"{process.ProcessName}.exe";
        }
        catch
        {
            return $"PID {item.ProcessId}";
        }
    }

    private static bool IsExplorer(string name)
    {
        return string.Equals(name, "explorer", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "explorer.exe", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Explorador do Windows", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Windows Explorer", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddUnique(ICollection<string> values, string value)
    {
        if (!values.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            values.Add(value);
        }
    }

    private static string? FindInstallPath(string packageName, string packageId)
    {
        return FindMsixInstallPath(packageName, packageId)
               ?? FindUninstallInstallPath(packageName);
    }

    private static string? FindMsixInstallPath(string packageName, string packageId)
    {
        try
        {
            var manager = new PackageManager();
            foreach (var package in manager.FindPackagesForUser(string.Empty))
            {
                try
                {
                    if (package.IsFramework || package.IsResourcePackage)
                    {
                        continue;
                    }

                    var installPath = package.InstalledLocation?.Path;
                    if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
                    {
                        continue;
                    }

                    var identity = package.Id.Name;
                    if (NameMatches(identity, packageId))
                    {
                        return installPath;
                    }

                    if (NameMatches(package.DisplayName, packageName))
                    {
                        return installPath;
                    }
                }
                catch
                {
                    // Some packages deny access to identity or display properties.
                }
            }
        }
        catch
        {
            // Fall through to the uninstall registry when AppX enumeration is unavailable.
        }

        return null;
    }

    private static string? FindUninstallInstallPath(string packageName)
    {
        var fallback = default(string);

        foreach (var root in OpenUninstallRoots())
        {
            using (root)
            {
                foreach (var subKeyName in root.GetSubKeyNames())
                {
                    try
                    {
                        using var key = root.OpenSubKey(subKeyName);
                        if (key is null)
                        {
                            continue;
                        }

                        var displayName = key.GetValue("DisplayName") as string;
                        if (!NameMatches(displayName, packageName))
                        {
                            continue;
                        }

                        var installLocation = SafeDirectory(key.GetValue("InstallLocation") as string);
                        if (installLocation is not null)
                        {
                            return installLocation;
                        }

                        var iconDirectory = SafeDirectory(IconPath(key.GetValue("DisplayIcon") as string));
                        fallback ??= iconDirectory;

                        fallback ??= SafeDirectory(UninstallerPath(key.GetValue("UninstallString") as string));
                    }
                    catch
                    {
                        // Ignore entries with malformed values.
                    }
                }
            }
        }

        return fallback;
    }

    private static IEnumerable<RegistryKey> OpenUninstallRoots()
    {
        var locations = new (RegistryHive Hive, RegistryView View)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
            (RegistryHive.CurrentUser, RegistryView.Registry32)
        };

        foreach (var (hive, view) in locations)
        {
            RegistryKey? root = null;
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                root = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            }
            catch
            {
                root?.Dispose();
                root = null;
            }

            if (root is not null)
            {
                yield return root;
            }
        }
    }

    private static string? IconPath(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
        {
            return null;
        }

        var value = displayIcon.Trim();
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            if (end > 1)
            {
                return value[1..end];
            }
        }

        var comma = value.LastIndexOf(',');
        if (comma > 0 && int.TryParse(value[(comma + 1)..].Trim(), out _))
        {
            value = value[..comma];
        }

        return value.Trim().Trim('"');
    }

    private static string? UninstallerPath(string? uninstallString)
    {
        if (string.IsNullOrWhiteSpace(uninstallString))
        {
            return null;
        }

        var value = uninstallString.Trim();
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            if (end > 1)
            {
                return value[1..end];
            }
        }

        var space = value.IndexOf(' ');
        return space > 0 ? value[..space] : value;
    }

    private static string? SafeDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            if (!Path.IsPathRooted(expanded))
            {
                return null;
            }

            var fullPath = Path.GetFullPath(expanded);

            if (File.Exists(fullPath))
            {
                fullPath = Path.GetDirectoryName(fullPath) ?? string.Empty;
            }

            if (!Directory.Exists(fullPath))
            {
                return null;
            }

            fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetPathRoot(fullPath)?.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);

            if (string.IsNullOrWhiteSpace(fullPath) ||
                string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            foreach (var blocked in BlockedDirectories)
            {
                if (!string.IsNullOrWhiteSpace(blocked) &&
                    string.Equals(fullPath, blocked.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            return fullPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Finds running services whose executable lives inside the package folder.
    /// Restart Manager does not always report packaged-process services, so the
    /// service database is checked directly.
    /// </summary>
    private static IReadOnlyList<string> FindServicesInFolder(string installPath)
    {
        var services = new List<string>();

        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (root is null)
            {
                return services;
            }

            foreach (var serviceName in root.GetSubKeyNames())
            {
                try
                {
                    using var serviceKey = root.OpenSubKey(serviceName);
                    var imagePath = serviceKey?.GetValue("ImagePath") as string;

                    if (string.IsNullOrWhiteSpace(imagePath))
                    {
                        continue;
                    }

                    var executable = ExtractExecutablePath(imagePath);
                    if (executable is null || !IsPathInsideFolder(executable, installPath))
                    {
                        continue;
                    }

                    if (WindowsServiceControl.IsRunning(serviceName))
                    {
                        AddUnique(services, serviceName);
                    }
                }
                catch
                {
                    // Services with unreadable configuration are ignored.
                }
            }
        }
        catch
        {
            // The service database is unavailable; Restart Manager results still apply.
        }

        return services;
    }

    private static string? ExtractExecutablePath(string imagePath)
    {
        var value = imagePath.Trim();

        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            return end > 1 ? value[1..end] : null;
        }

        if (value.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            value = Path.Combine(windows, value[@"\SystemRoot\".Length..]);
        }
        else if (value.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            value = value[4..];
        }

        var exeIndex = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex < 0)
        {
            return null;
        }

        return value[..(exeIndex + 4)].Trim();
    }

    private static bool IsPathInsideFolder(string path, string folder)
    {
        try
        {
            var normalizedPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedFolder = Path.GetFullPath(folder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return normalizedPath.StartsWith(
                normalizedFolder + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<string> CollectPackageFiles(string installPath)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(installPath);

        while (pending.Count > 0 && files.Count < MaxRegisteredFiles)
        {
            var directory = pending.Pop();

            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    var extension = Path.GetExtension(file);
                    if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".ocx", StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(file);
                        if (files.Count >= MaxRegisteredFiles)
                        {
                            break;
                        }
                    }
                }

                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    try
                    {
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                        {
                            pending.Push(child);
                        }
                    }
                    catch
                    {
                        // Skip folders whose attributes cannot be read.
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Some package folders restrict enumeration; remaining folders are still checked.
            }
            catch (IOException)
            {
                // The folder may disappear while the package is being updated.
            }
            catch (SecurityException)
            {
                // Same as above: keep scanning the rest of the package.
            }
        }

        return files;
    }

    private static bool NameMatches(string? candidate, string expected)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        var trimmedCandidate = candidate.Trim();
        var trimmedExpected = expected.Trim();

        if (string.Equals(trimmedCandidate, trimmedExpected, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalizedCandidate = NormalizeName(trimmedCandidate);
        var normalizedExpected = NormalizeName(trimmedExpected);

        return normalizedCandidate.Length > 2 &&
               string.Equals(normalizedCandidate, normalizedExpected, StringComparison.Ordinal);
    }

    private static string NormalizeName(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }
}
