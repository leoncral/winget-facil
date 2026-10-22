namespace Asa.Models;

/// <summary>
/// Raiz do catálogo curado (F1-7): categorias e entradas de segurança.
/// </summary>
public sealed class CatalogData
{
    public List<CatalogCategory> Categories { get; set; } = new();

    public List<CatalogSecurityEntry> Security { get; set; } = new();
}
