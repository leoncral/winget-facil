namespace Asa.Models;

/// <summary>
/// Origem da versão apresentada ao usuário para um pacote instalado.
/// </summary>
public enum InstalledVersionSource
{
    /// <summary>Versão informada pelo próprio winget.</summary>
    Winget,

    /// <summary>Versão lida do registro de desinstalação do Windows.</summary>
    Registry,

    /// <summary>Versão lida dos metadados do executável instalado.</summary>
    FileMetadata,

    /// <summary>Versão lida do executável apontado pela chave "App Paths" do registro.</summary>
    AppPath
}

public sealed class InstalledPackage
{
    public required string Name { get; init; }

    public required string Id { get; init; }

    /// <summary>Versão crua informada pelo winget (pode ser "Unknown").</summary>
    public required string Version { get; init; }

    public required string AvailableVersion { get; init; }

    public required string Source { get; init; }

    /// <summary>
    /// Versão detectada localmente (registro/metadados do executável). Nula quando não
    /// foi possível descobrir a versão com confiança suficiente.
    /// </summary>
    public string? DetectedVersion { get; init; }

    /// <summary>Origem da versão detectada localmente.</summary>
    public InstalledVersionSource DetectedVersionSource { get; init; } = InstalledVersionSource.Winget;

    /// <summary>Indica se a versão foi detectada localmente.</summary>
    public bool HasDetectedVersion => !string.IsNullOrWhiteSpace(DetectedVersion);

    /// <summary>
    /// Versão exibida ao usuário: a detectada localmente quando existe, senão a crua do winget.
    /// </summary>
    public string EffectiveVersion => HasDetectedVersion ? DetectedVersion! : Version;

    public bool HasUpdate => !string.IsNullOrWhiteSpace(AvailableVersion);

    /// <summary>
    /// Indica que a versão detectada localmente já é a mesma release disponível no catálogo.
    /// Nesse caso não há atualização real a oferecer.
    /// </summary>
    public bool IsDetectedVersionCurrent =>
        HasDetectedVersion &&
        !IsTrivialMetadataVersion &&
        VersionComparison.IsSameRelease(DetectedVersion!, AvailableVersion);

    /// <summary>
    /// Versão detectada em metadados de arquivo que não descreve a versão real do programa
    /// (0.0.0.0, 1.0.0.0...). Vale tanto para <see cref="InstalledVersionSource.FileMetadata"/>
    /// quanto para <see cref="InstalledVersionSource.AppPath"/>, pois as duas fontes leem os
    /// metadados do executável. Nesse caso a versão pode ser exibida, mas a atualização
    /// precisa continuar disponível. Versões vindas do registro seguem a comparação normal.
    /// </summary>
    private bool IsTrivialMetadataVersion =>
        (DetectedVersionSource is InstalledVersionSource.FileMetadata or InstalledVersionSource.AppPath) &&
        VersionComparison.IsTrivialVersion(DetectedVersion ?? string.Empty);

    public bool CanUpdate =>
        HasUpdate &&
        !string.IsNullOrWhiteSpace(Source) &&
        !IsDetectedVersionCurrent;

    public bool HasUnknownVersion => IsUnknown(EffectiveVersion);

    public string CurrentVersionDisplay => WingetDisplay.Version(EffectiveVersion);

    public string AvailableVersionDisplay => WingetDisplay.Version(AvailableVersion);

    public string SourceDisplay => string.IsNullOrWhiteSpace(Source) ? "local" : Source;

    public string Initial => WingetDisplay.Initial(Name);

    public string ActionText => CanUpdate ? "Atualizar" : "Em dia";

    /// <summary>Descrição curta da origem da versão detectada, em PT-BR.</summary>
    public string DetectedVersionSourceDisplay => DetectedVersionSource switch
    {
        InstalledVersionSource.Registry => "registro do Windows",
        InstalledVersionSource.FileMetadata => "arquivo do programa",
        InstalledVersionSource.AppPath => "caminho do aplicativo",
        _ => "winget"
    };

    /// <summary>Legenda da coluna de versão instalada. Fica fixa ("instalada") porque a
    /// legenda "instalada · verificada" não caberia na largura fixa de 110 px sem cortar;
    /// a verificação local é sinalizada pelo escudo junto da versão no template.</summary>
    public string CurrentVersionCaption => "instalada";

    /// <summary>
    /// Texto explicativo da versão apresentada: quando a versão foi detectada localmente,
    /// mostra a versão e onde ela foi verificada; sem detecção, mostra só a versão.
    /// </summary>
    public string CurrentVersionToolTip => HasDetectedVersion
        ? $"{CurrentVersionDisplay} · versão verificada no {DetectedVersionSourceDisplay}"
        : CurrentVersionDisplay;

    /// <summary>
    /// Cria uma cópia do pacote preenchendo a versão detectada localmente, preservando
    /// todas as demais propriedades.
    /// </summary>
    public InstalledPackage WithDetectedVersion(string version, InstalledVersionSource source)
    {
        var detected = string.IsNullOrWhiteSpace(version) ? null : version.Trim();

        return new InstalledPackage
        {
            Name = Name,
            Id = Id,
            Version = Version,
            AvailableVersion = AvailableVersion,
            Source = Source,
            DetectedVersion = detected,
            DetectedVersionSource = detected is null ? InstalledVersionSource.Winget : source
        };
    }

    private static bool IsUnknown(string value)
    {
        return string.IsNullOrWhiteSpace(value) ||
               string.Equals(value, "Unknown", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "Desconhecida", StringComparison.OrdinalIgnoreCase);
    }
}
