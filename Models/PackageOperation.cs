using Asa.Helpers;

namespace Asa.Models;

public enum PackageOperationKind
{
    Install,
    Upgrade
}

public sealed record PackageOperation(
    string Name,
    string Id,
    string Version,
    string Source,
    PackageOperationKind Kind)
{
    public string ActionLabel => Kind == PackageOperationKind.Install ? "Instalando" : "Atualizando";
}
