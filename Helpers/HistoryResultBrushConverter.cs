using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Asa.Helpers;

/// <summary>
/// Escolhe o pincel do chip de resultado de um lote no Histórico (verde = sucesso,
/// âmbar = atenção/parcial/cancelado, vermelho = falha). O conversor apenas ESCOLHE
/// recursos de <see cref="Styles.Tokens.xaml"/>; nenhuma cor é definida aqui.
/// <c>ConverterParameter</c> aceita "background" (padrão) ou "foreground".
/// </summary>
public sealed class HistoryResultBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var result = value as string ?? string.Empty;
        var wantsForeground = string.Equals(parameter as string, "foreground", StringComparison.OrdinalIgnoreCase);

        var key = Tone(result) switch
        {
            ResultTone.Success => wantsForeground ? "AsaOnUpdateBrush" : "AsaUpdateBrush",
            ResultTone.Caution => wantsForeground ? "AsaOnCautionBrush" : "AsaCautionBrush",
            ResultTone.Failure => wantsForeground ? "AsaOnErrorBrush" : "AsaErrorBrush",
            _ => wantsForeground ? "AsaTextSecondaryBrush" : "AsaSurfaceActiveBrush",
        };

        return ThemeResourceHelper.GetBrush(key) ?? new SolidColorBrush();
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();

    private static ResultTone Tone(string result)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            return ResultTone.Neutral;
        }

        if (result.StartsWith("Sucesso", StringComparison.OrdinalIgnoreCase))
        {
            return result.Contains("ignorado", StringComparison.OrdinalIgnoreCase)
                ? ResultTone.Caution
                : ResultTone.Success;
        }

        if (result.StartsWith("Parcial", StringComparison.OrdinalIgnoreCase) ||
            result.StartsWith("Cancel", StringComparison.OrdinalIgnoreCase))
        {
            return ResultTone.Caution;
        }

        if (result.StartsWith("Falh", StringComparison.OrdinalIgnoreCase) ||
            result.StartsWith("Falha", StringComparison.OrdinalIgnoreCase) ||
            result.StartsWith("Erro", StringComparison.OrdinalIgnoreCase))
        {
            return ResultTone.Failure;
        }

        return ResultTone.Neutral;
    }

    private enum ResultTone
    {
        Neutral,
        Success,
        Caution,
        Failure
    }
}
