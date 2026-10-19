namespace Asa.Models;

public sealed class InstalledPackage
{
    public required string Name { get; init; }

    public required string Id { get; init; }

    public required string Version { get; init; }

    public required string AvailableVersion { get; init; }

    public required string Source { get; init; }

    public bool HasUpdate => !string.IsNullOrWhiteSpace(AvailableVersion);

    public bool CanUpdate => HasUpdate && !string.IsNullOrWhiteSpace(Source);

    public string CurrentVersionDisplay => WingetDisplay.Version(Version);

    public string AvailableVersionDisplay => WingetDisplay.Version(AvailableVersion);

    public string SourceDisplay => string.IsNullOrWhiteSpace(Source) ? "local" : Source;

    public string Initial => WingetDisplay.Initial(Name);

    public string ActionText => CanUpdate ? "Atualizar" : "Em dia";
}
