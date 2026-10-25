using Asa.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Asa.Views;

public sealed partial class HistoryPage : Page
{
    private readonly DispatcherTimer _statusTimer;
    private bool _subscribed;

    public HistoryPage()
    {
        ViewModel = new HistoryViewModel(App.History);
        InitializeComponent();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += (_, _) => HideStatus();
    }

    public HistoryViewModel ViewModel { get; }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            App.History.Changed += History_Changed;
            _subscribed = true;
        }

        try
        {
            await ViewModel.LoadAsync();
        }
        catch
        {
            // A lista anterior permanece; o usuário vê o aviso e pode tentar de novo com F5.
            ShowStatus("Não foi possível carregar o histórico.", error: true);
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _statusTimer.Stop();

        if (_subscribed)
        {
            App.History.Changed -= History_Changed;
            _subscribed = false;
        }
    }

    /// <summary>
    /// O HistoryStore grava em thread de thread pool; recarrega a lista na UI.
    /// </summary>
    private void History_Changed(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await ViewModel.LoadAsync();
            }
            catch
            {
                // A próxima navegação/F5 tenta novamente; nunca derruba a página.
                ShowStatus("Não foi possível atualizar o histórico.", error: true);
            }
        });
    }

    private void ToggleLog_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: HistoryEntryViewModel entry })
        {
            entry.IsExpanded = !entry.IsExpanded;
        }
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HistoryEntryViewModel entry })
        {
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(entry.LogText);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            ShowStatus("Log do lote copiado para a área de transferência.");
        }
        catch
        {
            ShowStatus("Não foi possível copiar o log.", error: true);
        }
    }

    private async void ExportLog_Click(object sender, RoutedEventArgs e)
    {
        var text = ViewModel.BuildExportText();
        var suggestedName = $"asa-historico-{DateTime.Now:yyyyMMdd-HHmmss}";

        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = suggestedName
            };
            picker.FileTypeChoices.Add("Arquivo de texto", new List<string> { ".txt" });

            if (App.MainWindow is { } window)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }

            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            await FileIO.WriteTextAsync(file, text);
            ShowStatus($"Log exportado para {file.Path}.");
        }
        catch
        {
            // Se o seletor não abrir (ex.: ambiente sem identidade de pacote), grava em
            // %LOCALAPPDATA%\Asa\exports e informa o caminho real — nunca simula sucesso.
            var exported = await TryExportToDefaultFolderAsync(text, suggestedName);
            ShowStatus(
                exported is null
                    ? "Não foi possível exportar o log. Tente novamente."
                    : $"Log salvo em {exported}.",
                error: exported is null);
        }
    }

    private void MsixWarning_Close(InfoBar sender, object args)
    {
        _ = ViewModel.DismissMsixWarningAsync();
    }

    private void GoToUpdates_Click(object sender, RoutedEventArgs e)
    {
        App.MainWindow?.NavigateToSection("updates");
    }

    private async void Refresh_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        try
        {
            await ViewModel.LoadAsync();
        }
        catch
        {
            ShowStatus("Não foi possível atualizar o histórico.", error: true);
        }
    }

    private void ShowStatus(string message, bool error = false)
    {
        ActionStatusText.Text = message;
        ActionStatusText.Foreground = error
            ? ThemeBrush("AsaErrorBrush")
            : ThemeBrush("AsaUpdateBrush");
        ActionStatusText.Visibility = Visibility.Visible;

        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void HideStatus()
    {
        _statusTimer.Stop();
        ActionStatusText.Visibility = Visibility.Collapsed;
    }

    private static Brush ThemeBrush(string key)
    {
        return Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush();
    }

    private static async Task<string?> TryExportToDefaultFolderAsync(string text, string suggestedName)
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Asa",
                "exports");
            Directory.CreateDirectory(folder);

            var path = Path.Combine(folder, suggestedName + ".txt");
            await File.WriteAllTextAsync(path, text);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
