using System.ComponentModel;
using System.Runtime.InteropServices;
using Asa.Views;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;
using Windows.UI;

namespace Asa;

public sealed partial class MainWindow : Window
{
    private static readonly SolidColorBrush SuccessBrush = new(Color.FromArgb(255, 16, 124, 16));
    private static readonly SolidColorBrush CriticalBrush = new(Color.FromArgb(255, 196, 43, 28));
    private static readonly SolidColorBrush CautionBrush = new(Color.FromArgb(255, 157, 93, 0));
    private readonly DispatcherTimer _activityHideTimer;
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Asa · winget fácil";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        ConfigureWindow();
        ApplyBackdrop();
        ApplyTitleBarColors();

        _activityHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        _activityHideTimer.Tick += ActivityHideTimer_Tick;

        RootGrid.ActualThemeChanged += (_, _) => ApplyTitleBarColors();
        RootGrid.Loaded += RootGrid_Loaded;

        App.Packages.Changed += Packages_Changed;
        App.Operations.PropertyChanged += Operations_PropertyChanged;
        App.Operations.QueueCompleted += Operations_QueueCompleted;

        NavView.SelectedItem = DiscoverNav;
        NavigateTo(typeof(DiscoverPage));
        UpdateActivityBar();
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await InitializeWingetAsync();
    }

    private async Task InitializeWingetAsync()
    {
        WingetStatusText.Text = "Verificando o winget…";
        WingetStatusDot.Fill = SuccessBrush;

        var version = await App.Winget.GetVersionAsync();
        if (string.IsNullOrWhiteSpace(version))
        {
            WingetStatusText.Text = "winget indisponível";
            WingetStatusDot.Fill = CriticalBrush;
            WingetMissingBar.IsOpen = true;
            return;
        }

        WingetStatusText.Text = $"winget {version}";
        WingetStatusDot.Fill = SuccessBrush;
        WingetMissingBar.IsOpen = false;

        await App.Packages.RefreshAsync();
        UpdateUpdatesBadge();
    }

    private void ConfigureWindow()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Asa.ico");
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }

            const int preferredWidth = 1200;
            const int preferredHeight = 800;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var dpi = GetDpiForWindow(hwnd);
            var scale = dpi <= 0 ? 1.0 : dpi / 96.0;
            var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var workArea = display.WorkArea;
            var width = (int)Math.Min(preferredWidth * scale, workArea.Width * 0.94);
            var height = (int)Math.Min(preferredHeight * scale, workArea.Height * 0.94);
            var x = workArea.X + (workArea.Width - width) / 2;
            var y = workArea.Y + (workArea.Height - height) / 2;
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));

            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = true;
                presenter.IsMaximizable = true;
                presenter.IsMinimizable = true;
            }
        }
        catch
        {
            // Window positioning is cosmetic; the app still runs if it fails.
        }
    }

    private void ApplyBackdrop()
    {
        try
        {
            if (MicaController.IsSupported())
            {
                SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
                RootGrid.Background = new SolidColorBrush(Colors.Transparent);
            }
            else
            {
                RootGrid.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
            }
        }
        catch
        {
            RootGrid.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        }
    }

    private void ApplyTitleBarColors()
    {
        try
        {
            var titleBar = AppWindow.TitleBar;
            var isDark = RootGrid.ActualTheme == ElementTheme.Dark;
            var foreground = isDark ? Colors.White : Color.FromArgb(255, 22, 22, 22);
            var inactive = isDark ? Color.FromArgb(255, 150, 150, 150) : Color.FromArgb(255, 110, 110, 110);

            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonHoverForegroundColor = foreground;
            titleBar.ButtonPressedForegroundColor = foreground;
            titleBar.ButtonInactiveForegroundColor = inactive;
            titleBar.ButtonHoverBackgroundColor = isDark
                ? Color.FromArgb(30, 255, 255, 255)
                : Color.FromArgb(20, 0, 0, 0);
            titleBar.ButtonPressedBackgroundColor = isDark
                ? Color.FromArgb(45, 255, 255, 255)
                : Color.FromArgb(35, 0, 0, 0);
        }
        catch
        {
            // Custom caption colors are optional.
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavigateTo(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItemContainer is not NavigationViewItem item)
        {
            return;
        }

        var pageType = item.Tag switch
        {
            "discover" => typeof(DiscoverPage),
            "installed" => typeof(InstalledPage),
            "updates" => typeof(UpdatesPage),
            _ => null
        };

        if (pageType is not null)
        {
            NavigateTo(pageType);
        }
    }

    private void NavigateTo(Type pageType)
    {
        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }

    private void Packages_Changed(object? sender, EventArgs e)
    {
        UpdateUpdatesBadge();
    }

    private void UpdateUpdatesBadge()
    {
        var count = App.Packages.UpdateCount;
        UpdatesBadge.Value = count;
        UpdatesBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Operations_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateActivityBar();
    }

    private async void Operations_QueueCompleted(object? sender, EventArgs e)
    {
        try
        {
            // Winget can take a moment to release its files after updating App Installer.
            await Task.Delay(600);
            await App.Packages.RefreshAsync(force: true);
            UpdateUpdatesBadge();
            ReportStaleUpgrades();
        }
        catch
        {
            // The activity bar already explains any operation failure.
        }
    }

    /// <summary>
    /// Winget can report success even when an MSIX update was blocked (for example, while the app
    /// or its shell extension is in use). Warn when a package still shows a pending update.
    /// </summary>
    private void ReportStaleUpgrades()
    {
        var upgrades = App.Operations.CompletedUpgrades;
        if (upgrades.Count == 0)
        {
            return;
        }

        var stillPending = new List<string>();
        foreach (var operation in upgrades)
        {
            var package = App.Packages.Installed.FirstOrDefault(
                candidate => string.Equals(candidate.Id, operation.Id, StringComparison.OrdinalIgnoreCase));

            if (package is not null && package.CanUpdate && !stillPending.Contains(package.Name))
            {
                stillPending.Add(package.Name);
            }
        }

        if (stillPending.Count > 0)
        {
            App.Operations.ReportStaleUpgrades(stillPending);
        }
    }

    private void UpdateActivityBar()
    {
        var operations = App.Operations;
        ActivityBar.Visibility = operations.IsActivityVisible ? Visibility.Visible : Visibility.Collapsed;

        if (!operations.IsActivityVisible)
        {
            ActivityProgress.IsActive = false;
            _activityHideTimer.Stop();
            return;
        }

        ActivityTitle.Text = operations.ActivityTitle;
        ActivityDetail.Text = operations.ActivityDetail;
        ActivityProgress.IsActive = operations.IsBusy;
        ActivityProgress.Visibility = operations.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        ActivityStatusIcon.Visibility = operations.IsBusy ? Visibility.Collapsed : Visibility.Visible;
        ActivityStatusIcon.Glyph = operations.HasError ? "\uE783" : operations.HasWarning ? "\uE7BA" : "\uE73E";
        ActivityStatusIcon.Foreground = operations.HasError
            ? CriticalBrush
            : operations.HasWarning
                ? CautionBrush
                : SuccessBrush;
        ActivityCloseButton.Visibility = operations.IsBusy ? Visibility.Collapsed : Visibility.Visible;

        if (operations.IsBusy || operations.HasError || operations.HasWarning)
        {
            _activityHideTimer.Stop();
        }
        else if (!_activityHideTimer.IsEnabled)
        {
            _activityHideTimer.Start();
        }
    }

    private void ActivityHideTimer_Tick(object? sender, object e)
    {
        _activityHideTimer.Stop();
        if (!App.Operations.IsBusy)
        {
            App.Operations.DismissActivity();
        }
    }

    private void ActivityClose_Click(object sender, RoutedEventArgs e)
    {
        _activityHideTimer.Stop();
        App.Operations.DismissActivity();
    }

    private async void ActivityLog_Click(object sender, RoutedEventArgs e)
    {
        _activityHideTimer.Stop();

        var logText = new TextBlock
        {
            Text = App.Operations.GetLogText(),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };

        var scrollViewer = new ScrollViewer
        {
            Content = logText,
            MaxHeight = 430,
            MinWidth = 520,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled
        };

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Detalhes da operação",
            Content = scrollViewer,
            PrimaryButtonText = "Copiar",
            CloseButtonText = "Fechar",
            DefaultButton = ContentDialogButton.Close,
            MaxWidth = 760
        };

        dialog.PrimaryButtonClick += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(App.Operations.GetLogText());
            Clipboard.SetContent(package);
            Clipboard.Flush();
        };

        await dialog.ShowAsync();
    }

    private async void GetWinget_Click(object sender, RoutedEventArgs e)
    {
        await Launcher.LaunchUriAsync(new Uri("https://aka.ms/getwinget"));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
