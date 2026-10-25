using System;
using System.Globalization;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Asa.Helpers;

/// <summary>
/// Converte <see cref="bool"/> (esta linha está selecionada) no pincel de fundo da linha:
/// <see cref="AsaSurfaceActiveBrush"/> quando selecionada, <c>Transparent</c> quando não.
/// O fundo transparente não trava o preenchimento de PointerOver
/// (<see cref="AsaSurfaceHoverBrush"/>), que continua visível nas linhas não selecionadas.
/// Não usa cor literal; tudo vem dos tokens de <see cref="Styles.Tokens.xaml"/>.
/// </summary>
public sealed class SelectedBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var selected = value is bool b && b;

        if (selected)
        {
            return ThemeResourceHelper.GetBrush("AsaSurfaceActiveBrush")
                ?? ThemeResourceHelper.GetBrush("AsaSurfaceCanvasBrush")
                ?? new SolidColorBrush();
        }

        return new SolidColorBrush();
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
