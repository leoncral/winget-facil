using System.Diagnostics;
using System.Security.Principal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace Asa.Views;

public sealed partial class SettingsPage : Page
{
    private static readonly SolidColorBrush SuccessBrush = new(Color.FromArgb(255, 16, 124, 16));
    private static readonly SolidColorBrush CriticalBrush = new(Color.FromArgb(255, 196, 43, 28));

    public SettingsPage()
    {
        InitializeComponent();
        SilentToggle.IsOn = App.Settings.SilentInstall;
        AppVersionText.Text = $"Versão {typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}";
        PermissionText.Text = IsRunningAsAdministrator()
            ? "Executando como administrador: as operações do winget não devem pedir permissão extra."
            : "Executando como usuário padrão: o Windows pode pedir permissão em algumas operações.";
    }

    private static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshWingetStatusAsync();
    }

    private void SilentToggle_Toggled(object sender, RoutedEventArgs e)
    {
        App.Settings.SilentInstall = SilentToggle.IsOn;
    }

    private async void CheckWinget_Click(object sender, RoutedEventArgs e)
    {
        await RefreshWingetStatusAsync();
    }

    private async Task RefreshWingetStatusAsync()
    {
        WingetStatusText.Text = "Verificando o winget…";
        WingetPathText.Text = string.Empty;
        WingetMissingBar.IsOpen = false;

        var version = await App.Winget.GetVersionAsync();
        if (string.IsNullOrWhiteSpace(version))
        {
            WingetStatusText.Text = "winget não encontrado";
            WingetPathText.Text = "Instale o App Installer e tente novamente.";
            WingetStatusDot.Fill = CriticalBrush;
            WingetMissingBar.IsOpen = true;
            return;
        }

        WingetStatusText.Text = $"winget {version}";
        WingetPathText.Text = App.Winget.ExecutablePath ?? "Executável localizado pelo PATH.";
        WingetStatusDot.Fill = SuccessBrush;
    }

    private async void GetWinget_Click(object sender, RoutedEventArgs e)
    {
        await Launcher.LaunchUriAsync(new Uri("https://aka.ms/getwinget"));
    }

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(App.Settings.SettingsPath);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folder}\"",
                UseShellExecute = true
            });
        }
        catch
        {
            // Opening Explorer is a convenience; failing is not critical.
        }
    }
}
