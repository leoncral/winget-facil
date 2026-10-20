using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Asa.Models;
using Microsoft.Win32;

namespace Asa.Services;

/// <summary>
/// Tenta descobrir a versão instalada de pacotes que o winget reporta como "Unknown".
///
/// O serviço é deliberadamente conservador: só resolve quando encontra uma correspondência
/// de ALTA confiança (nome normalizado idêntico) entre o pacote e uma entrada do registro de
/// desinstalação, um executável da instalação ou uma chave "App Paths". Nada é executado,
/// nenhum arquivo é alterado e nenhuma chamada de rede é feita — apenas leitura de registro
/// e metadados de arquivo.
///
/// Ordem de preferência:
/// 1. <c>DisplayVersion</c> da entrada de desinstalação com nome idêntico.
/// 2. Metadados (<c>ProductVersion</c>/<c>FileVersion</c>) dos executáveis da instalação.
/// 3. Chave "App Paths" cujo nome do executável seja igual ao nome do pacote.
///
/// Quando nenhuma etapa encontra uma correspondência confiável, o pacote permanece com a
/// versão desconhecida do winget (e continua elegível para atualização com --include-unknown).
/// </summary>
public sealed partial class InstalledVersionResolver
{
    /// <summary>Profundidade máxima da busca de executáveis dentro da pasta de instalação.</summary>
    private const int MaxSearchDepth = 3;

    /// <summary>Limite de executáveis lidos por pacote (protege contra pastas gigantes).</summary>
    private const int MaxFilesPerPackage = 400;

    /// <summary>Limite de pastas visitadas por pacote.</summary>
    private const int MaxDirectoriesPerPackage = 200;

    /// <summary>Pastas de instalador/ cache ignoradas durante a busca por executáveis.</summary>
    private static readonly string[] IgnoredDirectoryNames =
    [
        "setup",
        "update",
        "installer",
        "redist",
        "temp",
        "cache"
    ];

    /// <summary>
    /// Pastas cujo nome indica que o executável é um instalador: a pasta real do programa é
    /// a pasta imediatamente acima.
    /// </summary>
    private static readonly string[] InstallerFolderNames =
    [
        "setup",
        "install",
        "installer",
        "installers",
        "installshield",
        "_setup"
    ];

    private static readonly (RegistryHive Hive, RegistryView View)[] RegistryLocations =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64),
        (RegistryHive.LocalMachine, RegistryView.Registry32),
        (RegistryHive.CurrentUser, RegistryView.Registry64),
        (RegistryHive.CurrentUser, RegistryView.Registry32)
    ];

    /// <summary>
    /// Enriquece a lista de pacotes com as versões detectadas localmente. Pacotes cuja versão
    /// do winget já é conhecida são devolvidos sem alteração (o registro nem é lido).
    /// </summary>
    public async Task<IReadOnlyList<InstalledPackage>> ResolveUnknownVersionsAsync(
        IReadOnlyList<InstalledPackage> packages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packages);

        if (packages.Count == 0)
        {
            return packages;
        }

        var unknown = packages.Where(package => package.HasUnknownVersion).ToList();
        if (unknown.Count == 0)
        {
            return packages;
        }

        var detections = await Task
            .Run(() => DetectVersions(unknown, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        if (detections.Count == 0)
        {
            return packages;
        }

        var resolved = new List<InstalledPackage>(packages.Count);
        foreach (var package in packages)
        {
            resolved.Add(detections.TryGetValue(package, out var detection)
                ? package.WithDetectedVersion(detection.Version, detection.Source)
                : package);
        }

        return resolved;
    }

    private static Dictionary<InstalledPackage, Detection> DetectVersions(
        IReadOnlyList<InstalledPackage> packages,
        CancellationToken cancellationToken)
    {
        var detections = new Dictionary<InstalledPackage, Detection>();
        var entriesByName = IndexUninstallEntries(cancellationToken);

        foreach (var package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var detection = DetectForPackage(package, entriesByName, cancellationToken);
                if (detection is not null)
                {
                    detections[package] = detection;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Falha ao inspecionar um pacote não pode derrubar o refresh inteiro.
            }
        }

        return detections;
    }

    private static Detection? DetectForPackage(
        InstalledPackage package,
        IReadOnlyDictionary<string, List<UninstallEntry>> entriesByName,
        CancellationToken cancellationToken)
    {
        var nameKey = NormalizeName(package.Name);
        var entries = nameKey.Length > 0 && entriesByName.TryGetValue(nameKey, out var found)
            ? found
            : [];

        // Preferência 1: versão declarada no registro de desinstalação.
        var registryVersion = ResolveFromRegistryVersion(entries);
        if (registryVersion is not null)
        {
            return new Detection(registryVersion, InstalledVersionSource.Registry);
        }

        // Preferência 2: metadados dos executáveis da instalação.
        var fileVersion = ResolveFromFileMetadata(entries, package.Name, cancellationToken);
        if (fileVersion is not null)
        {
            return new Detection(fileVersion, InstalledVersionSource.FileMetadata);
        }

        // Preferência 3: chave "App Paths" com o mesmo nome do pacote.
        var appPathVersion = ResolveFromAppPaths(package.Name, cancellationToken);
        if (appPathVersion is not null)
        {
            return new Detection(appPathVersion, InstalledVersionSource.AppPath);
        }

        return null;
    }

    /// <summary>
    /// Lê o <c>DisplayVersion</c> das entradas com nome idêntico. Versões conflitantes
    /// (releases diferentes) deixam o pacote sem resolução.
    /// </summary>
    private static string? ResolveFromRegistryVersion(IReadOnlyList<UninstallEntry> entries)
    {
        var versions = new List<string>();

        foreach (var entry in entries)
        {
            var version = entry.DisplayVersion?.Trim();
            if (!IsRegistryVersion(version))
            {
                continue;
            }

            versions.Add(version!);
        }

        if (versions.Count == 0)
        {
            return null;
        }

        var selected = versions[0];
        foreach (var version in versions.Skip(1))
        {
            if (!VersionComparison.IsSameRelease(selected, version))
            {
                // Versões conflitantes: melhor não resolver do que exibir a errada.
                return null;
            }

            if (CompareVersions(version, selected) > 0)
            {
                selected = version;
            }
        }

        return selected;
    }

    /// <summary>
    /// Procura executáveis da instalação e usa a versão dos metadados deles.
    /// </summary>
    private static string? ResolveFromFileMetadata(
        IReadOnlyList<UninstallEntry> entries,
        string packageName,
        CancellationToken cancellationToken)
    {
        var roots = CollectInstallationDirectories(entries);
        if (roots.Count == 0)
        {
            return null;
        }

        var candidates = new List<string>();
        var budget = new SearchBudget();

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CollectExecutables(root, packageName, 0, candidates, budget, cancellationToken);
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // Candidatos de releases diferentes são ambíguos: não resolvemos.
        var releases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            releases.Add(ReleaseKey(candidate));
        }

        if (releases.Count > 1)
        {
            return null;
        }

        var selected = candidates[0];
        foreach (var candidate in candidates.Skip(1))
        {
            if (CompareVersions(candidate, selected) > 0)
            {
                selected = candidate;
            }
        }

        return selected;
    }

    /// <summary>
    /// Procura a chave "App Paths" cujo executável tenha exatamente o nome do pacote.
    /// </summary>
    private static string? ResolveFromAppPaths(string packageName, CancellationToken cancellationToken)
    {
        var target = NormalizeName(packageName);
        if (target.Length == 0)
        {
            return null;
        }

        foreach (var (hive, view) in RegistryLocations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            RegistryKey? root = null;
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                root = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
            }
            catch
            {
                root = null;
            }

            if (root is null)
            {
                continue;
            }

            using (root)
            {
                string[] keyNames;
                try
                {
                    keyNames = root.GetSubKeyNames();
                }
                catch
                {
                    continue;
                }

                foreach (var keyName in keyNames)
                {
                    try
                    {
                        // O nome da chave é conferido antes de abri-la: a lista de "App Paths"
                        // pode ter dezenas de entradas e só interessa a homônima do pacote.
                        var executableName = Path.GetFileNameWithoutExtension(keyName);
                        if (!string.Equals(NormalizeName(executableName), target, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        using var key = root.OpenSubKey(keyName);
                        if (key is null)
                        {
                            continue;
                        }

                        var path = key.GetValue(null) as string ?? key.GetValue("Path") as string;
                        var version = ReadVersionFromExecutable(path, packageName);
                        if (version is not null)
                        {
                            return version;
                        }
                    }
                    catch
                    {
                        // Entradas com valores malformados são ignoradas.
                    }
                }
            }
        }

        return null;
    }

    private static string? ReadVersionFromExecutable(string? path, string packageName)
    {
        var executable = ExtractExecutablePath(path);
        if (executable is null)
        {
            return null;
        }

        try
        {
            if (!File.Exists(executable))
            {
                return null;
            }

            var info = FileVersionInfo.GetVersionInfo(executable);
            var productName = NormalizeName(info.ProductName);
            if (productName.Length == 0 || !string.Equals(productName, NormalizeName(packageName), StringComparison.Ordinal))
            {
                return null;
            }

            return PickVersion(info);
        }
        catch
        {
            return null;
        }
    }

    private static void CollectExecutables(
        string directory,
        string packageName,
        int depth,
        List<string> candidates,
        SearchBudget budget,
        CancellationToken cancellationToken)
    {
        if (depth > MaxSearchDepth || !budget.TryVisitDirectory())
        {
            return;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "*.exe", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return;
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!budget.TryVisitFile())
            {
                return;
            }

            var version = ReadVersionFromCandidate(file, packageName);
            if (version is not null)
            {
                candidates.Add(version);
            }
        }

        if (depth == MaxSearchDepth)
        {
            return;
        }

        string[] subdirectories;
        try
        {
            subdirectories = Directory.GetDirectories(directory);
        }
        catch
        {
            return;
        }

        foreach (var subdirectory in subdirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsIgnoredDirectory(Path.GetFileName(subdirectory)) || !IsLocalPath(subdirectory))
            {
                continue;
            }

            CollectExecutables(subdirectory, packageName, depth + 1, candidates, budget, cancellationToken);
        }
    }

    /// <summary>
    /// Lê a versão de um executável candidato, exigindo que o produto (ProductName ou
    /// FileDescription) seja igual ao nome do pacote. O nome do arquivo não é usado: ele
    /// pode coincidir com o de outro produto.
    /// </summary>
    private static string? ReadVersionFromCandidate(string path, string packageName)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!MatchesProduct(info, packageName))
            {
                return null;
            }

            return PickVersion(info);
        }
        catch
        {
            // Arquivo sem metadados ou inacessível: apenas ignorado.
            return null;
        }
    }

    /// <summary>
    /// Correspondência de alta confiança: o <c>ProductName</c> ou o <c>FileDescription</c> do
    /// executável precisa ser igual ao nome do pacote (normalizado). O nome do arquivo NÃO é
    /// usado: ele pode coincidir com o de outro produto ("Notepad.exe" para "Notepad++").
    /// </summary>
    private static bool MatchesProduct(FileVersionInfo info, string packageName)
    {
        var target = NormalizeName(packageName);
        if (target.Length == 0)
        {
            return false;
        }

        if (string.Equals(NormalizeName(info.ProductName), target, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(NormalizeName(info.FileDescription), target, StringComparison.Ordinal);
    }

    /// <summary>
    /// Escolhe a versão do executável. Versões inúteis (não numéricas ou zeradas, como
    /// "0.0.0.0") são descartadas: se o <c>ProductVersion</c> não servir, tenta o
    /// <c>FileVersion</c>; sem nenhuma delas, o arquivo não informa versão.
    /// </summary>
    private static string? PickVersion(FileVersionInfo info)
    {
        var productVersion = info.ProductVersion?.Trim();
        if (IsUsableFileVersion(productVersion))
        {
            return productVersion;
        }

        var fileVersion = info.FileVersion?.Trim();
        return IsUsableFileVersion(fileVersion) ? fileVersion : null;
    }

    /// <summary>Versão de arquivo utilizável: numérica e não zerada.</summary>
    private static bool IsUsableFileVersion(string? value)
    {
        if (!IsNumericVersion(value))
        {
            return false;
        }

        if (!VersionComparison.TryParseComponents(value, out var components))
        {
            return false;
        }

        foreach (var component in components)
        {
            if (component != 0)
            {
                return true;
            }
        }

        // "0.0.0.0" não descreve a versão do programa.
        return false;
    }

    /// <summary>
    /// Monta as pastas candidatas a partir de InstallLocation, DisplayIcon e UninstallString.
    /// </summary>
    private static List<string> CollectInstallationDirectories(IReadOnlyList<UninstallEntry> entries)
    {
        var directories = new List<string>();

        foreach (var entry in entries)
        {
            AddInstallationDirectory(directories, entry.InstallLocation);

            foreach (var value in new[] { entry.DisplayIcon, entry.UninstallString })
            {
                var executable = ExtractExecutablePath(value);
                if (executable is null || !TryGetDirectory(executable, out var directory))
                {
                    continue;
                }

                AddInstallationDirectory(directories, directory);
            }
        }

        return directories;
    }

    /// <summary>
    /// Adiciona uma pasta candidata de instalação. A pasta de instalação é apenas uma DICA
    /// de caminho: quando ela tem nome de instalador ("Setup", "Install"), a própria pasta não
    /// é varrida — o setup.exe ali dentro é o instalador, não o programa — e só a pasta
    /// acima é usada.
    /// </summary>
    private static void AddInstallationDirectory(ICollection<string> directories, string? path)
    {
        var directory = SafeDirectory(path);
        if (directory is null)
        {
            return;
        }

        if (!IsInstallerFolderName(Path.GetFileName(directory)))
        {
            AddUniqueDirectory(directories, directory);
            return;
        }

        // Pasta de instalador: sobe até a primeira pasta que não seja de instalador.
        var candidate = directory;
        while (TryGetDirectory(candidate, out var parentPath))
        {
            var parent = SafeDirectory(parentPath);
            if (parent is null)
            {
                return;
            }

            if (!IsInstallerFolderName(Path.GetFileName(parent)))
            {
                AddUniqueDirectory(directories, parent);
                return;
            }

            candidate = parent;
        }
    }

    private static void AddUniqueDirectory(ICollection<string> directories, string directory)
    {
        if (directories.Any(existing => string.Equals(existing, directory, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        directories.Add(directory);
    }

    /// <summary>Obtém a pasta de um caminho, ignorando valores malformados.</summary>
    private static bool TryGetDirectory(string path, out string directory)
    {
        try
        {
            directory = Path.GetDirectoryName(path) ?? string.Empty;
            return directory.Length > 0;
        }
        catch
        {
            directory = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Valida e normaliza um caminho de pasta.
    /// Expande variáveis de ambiente, aceita um caminho de arquivo existente (usa a pasta
    /// dele) e rejeita raízes de unidade, caminhos de rede (UNC) e caminhos inexistentes.
    /// </summary>
    private static string? SafeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            // O valor pode vir entre aspas e com argumentos depois
            // (ex.: "%ProgramFiles%\App\app.exe" /S).
            var candidate = ExtractQuotedPath(path);
            if (candidate.Length == 0)
            {
                return null;
            }

            // Variáveis de ambiente (%ProgramFiles%, %LOCALAPPDATA%...) são comuns no registro.
            var expanded = StripSurroundingQuotes(ExpandEnvironment(candidate).Trim());
            if (expanded.Length == 0 || !Path.IsPathRooted(expanded) || !IsLocalPath(expanded))
            {
                return null;
            }

            var fullPath = Path.GetFullPath(expanded);

            if (File.Exists(fullPath))
            {
                // O valor aponta para um arquivo: a pasta dele é a pasta de instalação.
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
                // Raiz de unidade ("C:\") nunca é uma pasta de instalação.
                return null;
            }

            return fullPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extrai o caminho de um valor que pode vir entre aspas, ignorando o que vier depois
    /// (argumentos de linha de comando).
    /// </summary>
    private static string ExtractQuotedPath(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith('"'))
        {
            return trimmed;
        }

        var end = trimmed.IndexOf('"', 1);
        return end > 1 ? trimmed[1..end] : trimmed.Trim('"');
    }

    /// <summary>Remove um par de aspas que involves o caminho inteiro.</summary>
    private static string StripSurroundingQuotes(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1].Trim();
        }

        return value;
    }

    private static string ExpandEnvironment(string value)
    {
        try
        {
            return Environment.ExpandEnvironmentVariables(value);
        }
        catch
        {
            return value;
        }
    }

    private static bool IsLocalPath(string path)
    {
        // Caminhos de rede (\\server\share) não são lidos.
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        // Unidades mapeadas na rede também são descartadas: evita I/O em caminho remoto.
        try
        {
            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network)
            {
                return false;
            }
        }
        catch
        {
            // Sem informação de unidade (caminho inexistente): a validação de existência
            // decide mais adiante.
        }

        return true;
    }

    private static bool IsIgnoredDirectory(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        return IgnoredDirectoryNames.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Indica se a pasta tem nome típico de instalador/setup — nesse caso a pasta acima é a
    /// pasta real do programa. "Bin" NÃO entra na lista: é comum em instalações reais e
    /// a pasta acima nem sempre é a instalação.
    /// </summary>
    private static bool IsInstallerFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return InstallerFolderNames.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extrai o caminho do executável de valores como
    /// <c>"C:\...\setup.exe",0</c> ou <c>C:\...\setup.exe /R</c>, expandindo variáveis de
    /// ambiente (<c>%ProgramFiles%\App\app.exe</c>).
    /// </summary>
    private static string? ExtractExecutablePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();

        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? ExpandEnvironment(text[1..end].Trim()) : null;
        }

        // DisplayIcon pode trazer o índice do ícone depois de uma vírgula.
        var comma = text.LastIndexOf(',');
        if (comma > 0 &&
            int.TryParse(text[(comma + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            text = text[..comma].Trim();
        }

        if (text.Length == 0)
        {
            return null;
        }

        // Comandos com argumentos ("C:\...\setup.exe /R"): o caminho é a parte até o .exe.
        var executableIndex = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (executableIndex > 0)
        {
            return ExpandEnvironment(text[..(executableIndex + 4)].Trim().Trim('"'));
        }

        return ExpandEnvironment(text);
    }

    private static Dictionary<string, List<UninstallEntry>> IndexUninstallEntries(CancellationToken cancellationToken)
    {
        var entriesByName = new Dictionary<string, List<UninstallEntry>>(StringComparer.Ordinal);

        foreach (var entry in ReadUninstallEntries(cancellationToken))
        {
            var key = NormalizeName(entry.DisplayName);
            if (key.Length == 0)
            {
                continue;
            }

            if (!entriesByName.TryGetValue(key, out var entries))
            {
                entries = [];
                entriesByName[key] = entries;
            }

            entries.Add(entry);
        }

        return entriesByName;
    }

    private static List<UninstallEntry> ReadUninstallEntries(CancellationToken cancellationToken)
    {
        var entries = new List<UninstallEntry>();

        foreach (var (hive, view) in RegistryLocations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            RegistryKey? root = null;
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                root = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            }
            catch
            {
                root = null;
            }

            if (root is null)
            {
                continue;
            }

            using (root)
            {
                string[] subKeyNames;
                try
                {
                    subKeyNames = root.GetSubKeyNames();
                }
                catch
                {
                    continue;
                }

                foreach (var subKeyName in subKeyNames)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        using var key = root.OpenSubKey(subKeyName);
                        if (key is null)
                        {
                            continue;
                        }

                        var displayName = (key.GetValue("DisplayName") as string)?.Trim();
                        if (string.IsNullOrEmpty(displayName))
                        {
                            continue;
                        }

                        entries.Add(new UninstallEntry(
                            displayName,
                            (key.GetValue("DisplayVersion") as string)?.Trim(),
                            (key.GetValue("InstallLocation") as string)?.Trim(),
                            (key.GetValue("DisplayIcon") as string)?.Trim(),
                            (key.GetValue("UninstallString") as string)?.Trim()));
                    }
                    catch
                    {
                        // Entradas com valores malformados são ignoradas.
                    }
                }
            }
        }

        return entries;
    }

    /// <summary>
    /// Normaliza um nome para comparação: sem acentos, em minúsculas, sem pontuação e com
    /// espaços colapsados. Sufixos de arquitetura entre parênteses são removidos.
    ///
    /// "++" e "#" fazem parte do nome do produto e por isso viram palavras ("plus", "sharp"):
    /// sem isso "Notepad++" e "Notepad" seriam o mesmo nome e o Asa poderia mostrar a
    /// versão do programa errado.
    /// </summary>
    private static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        var text = ArchitectureSuffixRegex().Replace(builder.ToString(), " ");

        // Sinais que fazem parte do nome do produto viram palavras antes da limpeza.
        text = text.Replace("+", " plus ", StringComparison.Ordinal);
        text = text.Replace("#", " sharp ", StringComparison.Ordinal);

        var result = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (pendingSpace && result.Length > 0)
                {
                    result.Append(' ');
                }

                pendingSpace = false;
                result.Append(character);
            }
            else
            {
                pendingSpace = true;
            }
        }

        return result.ToString();
    }

    /// <summary>Indica se o texto parece uma versão numérica pontuada (ex.: "6.7.81.39976").</summary>
    private static bool IsNumericVersion(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && NumericVersionRegex().IsMatch(value.Trim());
    }

    /// <summary>
    /// Indica se o texto do registro pode ser usado como versão de alta confiança.
    /// Só versões numéricas pontuadas são aceitas (ex.: "14.51.36247.0"); textos como
    /// "1.0 beta" não são versões confiáveis e o resolvedor prefere os metadados do arquivo.
    /// </summary>
    private static bool IsRegistryVersion(string? value)
    {
        return IsNumericVersion(value);
    }

    /// <summary>Chave da release (três primeiros componentes) usada para detectar conflitos.</summary>
    private static string ReleaseKey(string version)
    {
        if (VersionComparison.TryParseComponents(version, out var components))
        {
            var count = Math.Min(3, components.Length);
            var builder = new StringBuilder();
            for (var index = 0; index < count; index++)
            {
                if (index > 0)
                {
                    builder.Append('.');
                }

                builder.Append(components[index].ToString(CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        return version.Trim().ToLowerInvariant();
    }

    private static int CompareVersions(string left, string right)
    {
        if (VersionComparison.TryParseComponents(left, out var leftComponents) &&
            VersionComparison.TryParseComponents(right, out var rightComponents))
        {
            var count = Math.Max(leftComponents.Length, rightComponents.Length);
            for (var index = 0; index < count; index++)
            {
                var leftValue = index < leftComponents.Length ? leftComponents[index] : 0;
                var rightValue = index < rightComponents.Length ? rightComponents[index] : 0;
                if (leftValue != rightValue)
                {
                    return leftValue.CompareTo(rightValue);
                }
            }

            return 0;
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^\d+(\.\d+){0,4}$")]
    private static partial Regex NumericVersionRegex();

    [GeneratedRegex(@"\(\s*(x64|x86|amd64|arm64|ia64|64-?bit|32-?bit)\s*\)")]
    private static partial Regex ArchitectureSuffixRegex();

    private sealed record Detection(string Version, InstalledVersionSource Source);

    private sealed record UninstallEntry(
        string DisplayName,
        string? DisplayVersion,
        string? InstallLocation,
        string? DisplayIcon,
        string? UninstallString);

    private sealed class SearchBudget
    {
        private int _files;
        private int _directories;

        public bool TryVisitFile() => ++_files <= MaxFilesPerPackage;

        public bool TryVisitDirectory() => ++_directories <= MaxDirectoriesPerPackage;
    }
}
