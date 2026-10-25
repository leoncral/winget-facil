using Asa.Services;
using Asa.ViewModels;
using Microsoft.UI.Xaml;

namespace Asa;

public partial class App : Application
{
    public static SettingsStore Settings { get; } = new();

    public static WingetCli Winget { get; } = new();

    public static InstalledVersionResolver VersionResolver { get; } = new();

    public static PackageStore Packages { get; } = new(Winget, VersionResolver);

    public static RunningAppGuard RunningApps { get; } = new();

    public static HistoryStore History { get; } = new();

    public static OperationCenter Operations { get; } = new(Winget, Settings, RunningApps, History);

    public static CatalogService Catalog { get; } = new();

    public static WingetMetadataService Metadata { get; } = new();

    // Escopo único: as páginas de lista compartilham estas instâncias para que seleção,
    // filtro e linhas sobrevivam a navegações (em vez de criar um ViewModel por página).
    public PackagesViewModel InstalledPackages { get; } = new(Packages, Operations, updatesOnlyMode: false);

    public PackagesViewModel UpdatesPackages { get; } = new(Packages, Operations, updatesOnlyMode: true);

    private Window? _window;

    public App()
    {
        UnhandledException += App_UnhandledException;
        InitializeComponent();
    }

    private static void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Asa");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "crash.log"),
                $"[{DateTime.Now:dd/MM/yyyy HH:mm:ss}] {e.Message}{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never mask the original failure.
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        // Inicializa a mesma instancia de WindowMetricsService exposta como recurso
        // estatico em App.xaml (origem das ligacoes {Binding ... Source={StaticResource WindowMetrics}}).
        ((WindowMetricsService)Resources["WindowMetrics"]!).Initialize(_window);
        _window.Activate();
    }
}
