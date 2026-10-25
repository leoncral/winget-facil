using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace Asa.Helpers;

/// <summary>
/// Resolve um recurso de pincel respeitando o tema atual.
/// <see cref="Application.Current.Resources"/> ignora os <c>ThemeDictionaries</c>, então,
/// em Alto Contraste, consultamos explicitamente o dicionário de tema <c>HighContrast</c>;
/// fora dele usamos o recurso de nível superior (tema escuro).
/// </summary>
internal static class ThemeResourceHelper
{
    // Lido dinamicamente a cada consulta: o usuário pode ativar/desativar o Alto Contraste
    // a qualquer momento e a captura estática ficaria presa no tema inicial.
    private static readonly AccessibilitySettings _accessibilitySettings = new();

    /// <summary>Obtém um <see cref="Brush"/> pelo nome da chave, adequando-se ao Alto Contraste.</summary>
    public static Brush? GetBrush(string key)
    {
        if (_accessibilitySettings.HighContrast)
        {
            var hc = ThemeDictionary("HighContrast");
            if (hc is not null && hc.TryGetValue(key, out var hcValue) && hcValue is Brush hcBrush)
            {
                return hcBrush;
            }
        }

        if (Application.Current.Resources.TryGetValue(key, out var appValue) && appValue is Brush appBrush)
        {
            return appBrush;
        }

        return new SolidColorBrush();
    }

    private static ResourceDictionary? ThemeDictionary(string themeName)
    {
        foreach (var dictionary in Application.Current.Resources.MergedDictionaries)
        {
            if (dictionary.ThemeDictionaries is not null &&
                dictionary.ThemeDictionaries.TryGetValue(themeName, out var themeEntry) &&
                themeEntry is ResourceDictionary theme)
            {
                return theme;
            }
        }

        return null;
    }
}
