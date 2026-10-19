namespace Asa.Models;

public static class WingetDisplay
{
    public static string Initial(string name)
    {
        foreach (var character in name)
        {
            if (char.IsLetterOrDigit(character))
            {
                return char.ToUpperInvariant(character).ToString();
            }
        }

        return "?";
    }

    public static string Version(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return "Desconhecida";
        }

        return value;
    }
}
