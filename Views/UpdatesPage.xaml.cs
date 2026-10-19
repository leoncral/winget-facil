using Asa.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Asa.Views;

public sealed partial class UpdatesPage : Page
{
    private bool _subscribed;

    public UpdatesPage()
    {
        ViewModel = new PackagesViewModel(App.Packages, App.Operations, updatesOnlyMode: true);
        InitializeComponent();
    }

    public PackagesViewModel ViewModel { get; }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            App.Packages.Changed += Packages_Changed;
            _subscribed = true;
        }

        await ViewModel.LoadAsync();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            App.Packages.Changed -= Packages_Changed;
            _subscribed = false;
        }
    }

    private void Packages_Changed(object? sender, EventArgs e)
    {
        ViewModel.RefreshFromStore();
    }

    private void FilterBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        ViewModel.SearchText = sender.Text;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox)
        {
            ViewModel.SetAllVisibleSelected(checkBox.IsChecked == true);
        }
    }

    private void UpdateSelected_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.QueueSelectedUpdates();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync(force: true);
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync(force: true);
    }

    private async void Refresh_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await ViewModel.LoadAsync(force: true);
    }

    private void FocusFilter_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        FilterBox.Focus(FocusState.Programmatic);
    }
}
