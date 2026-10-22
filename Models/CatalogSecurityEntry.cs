namespace Asa.Models;

/// <summary>
/// Entrada de segurança do catálogo curado (F1-7).
/// </summary>
public sealed class CatalogSecurityEntry
{
    public required string Id { get; init; }

    public required string Reason { get; set; }

    public required string Severity { get; init; }
}
