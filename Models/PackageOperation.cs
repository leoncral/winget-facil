using Asa.Helpers;

namespace Asa.Models;

public enum PackageOperationKind
{
    Install,
    Upgrade
}

/// <summary>
/// Uma operação da fila. <paramref name="InstalledVersion"/> é a versão instalada que
/// originou um upgrade (vazia para instalações): o mesmo Id pode aparecer em várias linhas
/// instaladas (por exemplo, .NET SDK lado a lado) e o comando do winget não distingue qual
/// delas atualizar. A identidade serve para atribuir os eventos de estado à linha correta,
/// não para mudar o comando.
/// </summary>
public sealed record PackageOperation(
    string Name,
    string Id,
    string Version,
    string Source,
    PackageOperationKind Kind,
    string InstalledVersion = "")
{
    public string ActionLabel => Kind == PackageOperationKind.Install ? "Instalando" : "Atualizando";
}
