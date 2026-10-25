using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Asa.Helpers;

/// <summary>
/// Escolhe um <see cref="LinearGradientBrush"/> (ou <see cref="SolidColorBrush"/> em
/// Alto Contraste) determinístico para o avatar de um pacote, a partir de seu
/// identificador. O mesmo input sempre produz o mesmo gradiente. Os gradientes são
/// definidos inteiramente em <see cref="Styles.Tokens.xaml"/>; este conversor apenas
/// escolhe o recurso pelo índice do hash estável. Nenhum hex literal aqui.
/// </summary>
public sealed class AvatarGradientConverter : IValueConverter
{
    /// <summary>Quantidade de gradientes definidos em Tokens.xaml (AsaAvatarGradient1..N).</summary>
    private const int GradientCount = 6;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var input = value as string ?? string.Empty;
        if (string.IsNullOrEmpty(input))
        {
            input = "?";
        }

        var index = StableHash(input) % GradientCount;
        var key = $"AsaAvatarGradient{index + 1}";

        return ThemeResourceHelper.GetBrush(key)
            ?? new SolidColorBrush();
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();

    private static int StableHash(string text)
    {
        // Hash FNV-1a simples e determinístico, independente de cultura.
        var hash = 2166136261u;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= 16777619u;
        }

        return (int)(hash & 0x7FFFFFFF);
    }
}
