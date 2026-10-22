using System.Globalization;
using System.Text.RegularExpressions;

namespace Asa.Models;

/// <summary>
/// Compara a versão detectada localmente com a versão disponível no catálogo do winget.
///
/// Regras (aplicadas nesta ordem):
/// 1. Textos normalizados iguais (trim e remoção do prefixo "v") => mesma versão.
/// 2. Versões numéricas com os mesmos valores, ignorando componentes finais zero
///    (por exemplo, "1.2" e "1.2.0.0") => mesma versão.
/// 3. Mesma release: os três primeiros componentes são iguais e a versão DISPONÍVEL não tem
///    quarto componente ou tem quarto componente zero. Exemplo real: a instalada
///    "6.7.81.39976" e a disponível "6.7.81.0" são a mesma release (o winget publica a
///    release em três componentes e o instalador grava o build em quatro).
/// 4. Quando a versão disponível tem quarto componente diferente de zero, a igualdade dos
///    três primeiros componentes NÃO basta: "1.2.3.4" e "1.2.3.5" são versões diferentes.
/// 5. Versões não numéricas só são iguais quando o texto normalizado é idêntico.
///
/// Observação: a comparação nunca esconde uma atualização quando a versão disponível está
/// vazia ou inválida — nesses casos o resultado é sempre falso.
/// </summary>
public static class VersionComparison
{
    /// <summary>Quantidade máxima de componentes numéricos aceitos em uma versão.</summary>
    private const int MaxNumericComponents = 5;

    /// <summary>
    /// Indica se a versão instalada e a versão disponível representam a mesma release.
    /// </summary>
    public static bool IsSameRelease(string installedVersion, string availableVersion)
    {
        if (string.IsNullOrWhiteSpace(installedVersion) || string.IsNullOrWhiteSpace(availableVersion))
        {
            // Sem versão disponível válida não é possível esconder a atualização.
            return false;
        }

        var installed = Normalize(installedVersion);
        var available = Normalize(availableVersion);

        if (IsPlaceholder(installed) || IsPlaceholder(available))
        {
            return false;
        }

        // Regra 1 (e regra 5 para versões não numéricas).
        if (string.Equals(installed, available, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!TryParseComponents(installed, out var installedComponents) ||
            !TryParseComponents(available, out var availableComponents))
        {
            // Regra 5: sem equivalente numérico, só o texto idêntico vale (tratado acima).
            return false;
        }

        // Regra 2: mesmos valores numéricos ignorando zeros finais.
        if (SameNumericValue(installedComponents, availableComponents))
        {
            return true;
        }

        // Regras 3 e 4: release/build.
        return IsSameReleaseBuild(installedComponents, availableComponents);
    }

    /// <summary>
    /// Indica se a versão é "trivial": versões numéricas cujos componentes são todos
    /// menores ou iguais a 1 (por exemplo, "0.0.0.0", "1.0.0.0", "1.1.0.0").
    /// Tais versões não descrevem a versão real do programa e, por isso, não devem
    /// esconder uma atualização. Versões não numéricas nunca são triviais.
    /// </summary>
    public static bool IsTrivialVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!TryParseComponents(value, out var components))
        {
            return false;
        }

        foreach (var component in components)
        {
            if (component > 1)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Converte uma versão numérica (por exemplo, "6.7.81.39976") em seus componentes.
    /// Retorna false quando o texto não é uma versão numérica pontuada.
    /// </summary>
    public static bool TryParseComponents(string? value, out int[] components)
    {
        components = [];

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Trim().Split('.');
        if (parts.Length is 0 or > MaxNumericComponents)
        {
            return false;
        }

        var numbers = new int[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index].Trim();
            if (part.Length == 0 || !part.All(char.IsAsciiDigit))
            {
                return false;
            }

            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index]))
            {
                return false;
            }
        }

        components = numbers;
        return true;
    }

    private static string Normalize(string value)
    {
        var text = value.Trim();

        // Prefixo comum em tags de versão ("v1.2.3").
        if (text.Length > 1 && (text[0] == 'v' || text[0] == 'V') && char.IsAsciiDigit(text[1]))
        {
            text = text[1..];
        }

        return text.Trim();
    }

    private static bool IsPlaceholder(string value)
    {
        return string.IsNullOrWhiteSpace(value) ||
               string.Equals(value, "Unknown", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "Desconhecida", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameNumericValue(int[] left, int[] right)
    {
        var normalizedLeft = TrimTrailingZeros(left);
        var normalizedRight = TrimTrailingZeros(right);

        if (normalizedLeft.Length != normalizedRight.Length)
        {
            return false;
        }

        for (var index = 0; index < normalizedLeft.Length; index++)
        {
            if (normalizedLeft[index] != normalizedRight[index])
            {
                return false;
            }
        }

        return true;
    }

    private static int[] TrimTrailingZeros(int[] components)
    {
        var length = components.Length;
        while (length > 0 && components[length - 1] == 0)
        {
            length--;
        }

        if (length == components.Length)
        {
            return components;
        }

        var trimmed = new int[length];
        Array.Copy(components, trimmed, length);
        return trimmed;
    }

    /// <summary>
    /// Palavras-chave que indicam uma versão de pré-lançamento (beta, alpha, rc, etc.).
    /// A detecção usa correspondência de palavra inteira: a palavra-chave deve estar
    /// delimitada por caracteres não alfanuméricos (ou início/fim da string) e não
    /// pode ser seguida por uma letra. Isso evita falsos positivos como "ea" dentro
    /// de "release", "pre" dentro de "premium" e "rc" dentro de "architecture",
    /// ao passo que ainda detecta "beta" em "6.0.0-beta4.1".
    /// </summary>
    private static readonly string[] PreReleaseKeywords =
    {
        "beta", "alpha", "rc", "preview", "insider", "dev", "nightly",
        "canary", "ea", "pre", "prerelease", "pre-release", "snapshot", "milestone"
    };

    /// <summary>
    /// Expressão regular que combina qualquer palavra-chave de pré-lançamento como
    /// palavra inteira (case-insensitive, cultura invariante).
    /// </summary>
    private static readonly Regex PreReleaseRegex = new(
        $"(?<![a-zA-Z0-9])(?:{string.Join("|", Array.ConvertAll(PreReleaseKeywords, Regex.Escape))})(?![a-zA-Z])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Indica se a versão é de pré-lançamento, com base em correspondência de
    /// palavra inteira das palavras-chave (beta, alpha, rc, preview, insider, dev,
    /// nightly, canary, ea, pre, prerelease, pre-release, snapshot, milestone),
    /// comparado de forma case-insensitive.
    /// Retorna false quando a versão é null/vazia ou não contém nenhuma dessas
    /// palavras como palavra inteira.
    /// </summary>
    public static bool IsPreRelease(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        return PreReleaseRegex.IsMatch(version);
    }

    /// <summary>
    /// Indica se a versão é estável, ou seja, não é de pré-lançamento.
    /// Retorna true para versões null/vazias (não há indício de pré-lançamento).
    /// </summary>
    public static bool IsStableVersion(string? version)
    {
        return !IsPreRelease(version);
    }

    private static bool IsSameReleaseBuild(int[] installed, int[] available)
    {
        if (installed.Length < 3 || available.Length < 3)
        {
            return false;
        }

        // Formatos com mais de quatro componentes não são comparáveis com segurança.
        if (available.Length > 4)
        {
            return false;
        }

        if (installed[0] != available[0] || installed[1] != available[1] || installed[2] != available[2])
        {
            return false;
        }

        // Regra 4: build disponível diferente de zero indica outra versão da mesma release.
        var availableBuild = available.Length > 3 ? available[3] : 0;
        return availableBuild == 0;
    }
}
