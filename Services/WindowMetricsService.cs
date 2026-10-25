using System;
using Asa.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;

namespace Asa.Services;

/// <summary>
/// Expõe a largura atual da janela principal de forma robusta (via
/// <see cref="Window.Bounds"/> e <see cref="AppWindow.Changed">, atualizando as
/// linhas da tabela quando a janela cruza o limite de colapso (1040 px).
/// </summary>
public sealed class WindowMetricsService : ObservableObject
{
    /// <summary>Largura abaixo da qual as colunas secundárias (ORIGEM) são ocultadas.</summary>
    public const double SidebarCollapseThreshold = 1040;

    private double _width;

    public double Width
    {
        get => _width;
        private set
        {
            if (SetProperty(ref _width, value))
            {
                OnPropertyChanged(nameof(IsNarrow));
                OnPropertyChanged(nameof(IsOriginColumnVisible));
                OnPropertyChanged(nameof(OriginColumnWidth));
            }
        }
    }

    /// <summary>Janela abaixo do limite de colapso da sidebar.</summary>
    public bool IsNarrow => Width < SidebarCollapseThreshold;

    /// <summary>Coluna ORIGEM deve ser exibida.</summary>
    public bool IsOriginColumnVisible => !IsNarrow;

    /// <summary>Largura da coluna ORIGEM (0 quando ocultada).</summary>
    public GridLength OriginColumnWidth => IsNarrow ? new GridLength(0) : new GridLength(96);

    public void Initialize(Window window)
    {
        if (window is null)
        {
            return;
        }

        Width = window.Bounds.Width;
        window.AppWindow.Changed += (_, _) =>
        {
            try
            {
                Width = window.Bounds.Width;
            }
            catch
            {
                // Redimensionamento é cosmético; ignora falhas de leitura deBounds.
            }
        };
    }
}
