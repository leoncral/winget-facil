using Asa.Models;
using Asa.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Asa.Views;

public sealed partial class DiscoverPage : Page
{
    private CancellationTokenSource? _searchCancellation;
    private bool _subscribed;

    public DiscoverPage()
    {
        ViewModel = new DiscoverViewModel(App.Winget, App.Packages, App.Operations);
        InitializeComponent();
    }

    public DiscoverViewModel ViewModel { get; }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            App.Packages.Changed += Packages_Changed;
            _subscribed = true;
        }

        SearchBox.Focus(FocusState.Programmatic);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            App.Packages.Changed -= Packages_Changed;
            _subscribed = false;
        }

        CancelActiveSearch();
    }

    private void Packages_Changed(object? sender, EventArgs e)
    {
        ViewModel.RefreshInstalledState();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text;
        if (string.IsNullOrWhiteSpace(query))
        {
            CancelActiveSearch();
            ViewModel.Reset();
            return;
        }

        if (query.Trim().Length < 2)
        {
            CancelActiveSearch();
            return;
        }

        QueueSearch(query, immediate: false);
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            QueueSearch(SearchBox.Text, immediate: true);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            SearchBox.Text = string.Empty;
        }
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        QueueSearch(SearchBox.Text, immediate: true);
    }

    private void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string suggestion })
        {
            return;
        }

        SearchBox.Text = suggestion;
        SearchBox.Focus(FocusState.Programmatic);
        QueueSearch(suggestion, immediate: true);
    }

    private void FocusSearch_Click(object sender, RoutedEventArgs e)
    {
        FocusSearch();
    }

    private void RetrySearch_Click(object sender, RoutedEventArgs e)
    {
        QueueSearch(SearchBox.Text, immediate: true);
    }

    private void FocusSearch_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        FocusSearch();
    }

    private void FocusSearch()
    {
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SearchPackage package } || !package.CanInstall)
        {
            return;
        }

        var queued = App.Operations.EnqueueInstall(package);
        package.IsQueued = queued || App.Operations.Contains(package.Id);
    }

    private void QueueSearch(string query, bool immediate)
    {
        CancelActiveSearch();

        var trimmed = query.Trim();
        if (trimmed.Length == 0)
        {
            ViewModel.Reset();
            return;
        }

        if (!immediate && trimmed.Length < 2)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        _ = RunSearchAsync(trimmed, immediate, cancellation);
    }

    private async Task RunSearchAsync(string query, bool immediate, CancellationTokenSource cancellation)
    {
        try
        {
            if (!immediate)
            {
                await Task.Delay(500, cancellation.Token);
            }

            await ViewModel.SearchAsync(query, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // A newer query replaced this request.
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
            {
                _searchCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void CancelActiveSearch()
    {
        var cancellation = _searchCancellation;
        _searchCancellation = null;
        cancellation?.Cancel();
    }
}
