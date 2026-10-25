using System;
using System.Globalization;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Asa.Helpers;

/// <summary>
/// Retorna o pincel de fundo do badge de origem de acordo com a origem do pacote
/// (winget / msstore / local), usando exclusivamente os tokens de
/// <see cref="Styles.Tokens.xaml"/>. O conversor apenas ESCOLHE o recurso; nenhuma
/// cor é definida aqui. O valor de entrada é a string da origem já normalizada
/// (<see cref="Asa.Models.InstalledPackage.OriginBadge"/>).
/// </summary>
public sealed class OriginBadgeBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var origin = value as string ?? string.Empty;
        var key = origin switch
        {
            "msstore" => "AsaOriginBadgeMsstoreBrush",
            "local" => "AsaOriginBadgeLocalBrush",
            _ => "AsaOriginBadgeLocalBrush",
        };

        return ThemeResourceHelper.GetBrush(key)
            ?? new SolidColorBrush();
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
