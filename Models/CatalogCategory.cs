namespace Asa.Models;

/// <summary>
/// Categoria do catálogo curado de pacotes (F1-7).
/// </summary>
public sealed class CatalogCategory
{
    public required string Key { get; init; }

    public required string Name { get; init; }

    public required List<string> Ids { get; init; }
}
