using Asa.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Asa.Views;

public sealed partial class InstalledPage : Page
{
    private bool _subscribed;

    public InstalledPage()
    {
        // ViewModel de escopo único, criado uma vez no App e compartilhado entre navegações.
        ViewModel = ((App)Application.Current).InstalledPackages;
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

        // Reaplica na UI o filtro que vive no ViewModel compartilhado.
        FilterBar.SelectedItem = ViewModel.ShowOnlyUpdates ? UpdatesFilter : AllFilter;
        if (!string.Equals(FilterBox.Text, ViewModel.SearchText, StringComparison.Ordinal))
        {
            FilterBox.Text = ViewModel.SearchText;
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

    private void FilterBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        ViewModel.ShowOnlyUpdates = !ReferenceEquals(sender.SelectedItem, AllFilter);
    }

    private void AllFilter_Click(object sender, RoutedEventArgs e) => SetFilter(sender, UpdatesFilterKind.All);

    private void CriticalFilter_Click(object sender, RoutedEventArgs e) => SetFilter(sender, UpdatesFilterKind.Critical);

    private void WingetFilter_Click(object sender, RoutedEventArgs e) => SetFilter(sender, UpdatesFilterKind.Winget);

    private void MsStoreFilter_Click(object sender, RoutedEventArgs e) => SetFilter(sender, UpdatesFilterKind.MsStore);

    private void SetFilter(object sender, UpdatesFilterKind filter)
    {
        // ToggleButton alterna o estado antes do Click; o chip selecionado deve continuar ativo.
        if (sender is ToggleButton toggleButton)
        {
            toggleButton.IsChecked = true;
        }

        ViewModel.UpdatesFilter = filter;
    }

    private void FilterBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        ViewModel.SearchText = sender.Text;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox)
        {
            // Estado indeterminado (parcial) clicado vira "marcar tudo".
            ViewModel.SetAllVisibleSelected(checkBox.IsChecked == true);
        }
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearSelection();
    }

    private void ClearFilter_Click(object sender, RoutedEventArgs e)
    {
        // O AutoSuggestBox não é bound: limpe o texto da UI e deixe o ViewModel limpar a
        // busca e, no escopo Atualizações, voltar o subfiltro para Todos. A aba ativa é
        // preservada (Instalados/Todos não é trocado para Atualizações).
        FilterBox.Text = string.Empty;
        ViewModel.ClearFilters();
    }

    private void UpdateSelected_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.QueueSelectedUpdates();
    }

    private void SelectionNotice_Close(InfoBar sender, object args)
    {
        ViewModel.DismissSelectionNotice();
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
