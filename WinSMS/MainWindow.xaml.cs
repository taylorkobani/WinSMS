using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinSMS.Models;
using WinSMS.ViewModels;
using WinSMS.Views;

namespace WinSMS;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        this.InitializeComponent();
        _viewModel = App.Services.GetRequiredService<MainViewModel>();
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateStatusIndicator(_viewModel.ConnectionState);
    }

    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ContentFrame.Navigate(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItem is NavigationViewItem item)
        {
            var tag = item.Tag?.ToString();
            var page = tag switch
            {
                "Inbox" => typeof(InboxPage),
                "Outbox" => typeof(OutboxPage),
                "Compose" => typeof(ComposePage),
                _ => typeof(InboxPage)
            };
            ContentFrame.Navigate(page);
        }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ConnectionState))
            UpdateStatusIndicator(_viewModel.ConnectionState);
        else if (e.PropertyName == nameof(MainViewModel.ConnectionStateText))
            StatusText.Text = _viewModel.ConnectionStateText;
    }

    private void UpdateStatusIndicator(ModemConnectionState state)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            StatusText.Text = _viewModel.ConnectionStateText;
            StatusIndicator.Fill = state switch
            {
                ModemConnectionState.Connected => new SolidColorBrush(Colors.Green),
                ModemConnectionState.Connecting => new SolidColorBrush(Colors.Orange),
                ModemConnectionState.Error => new SolidColorBrush(Colors.Red),
                _ => new SolidColorBrush(Colors.Gray)
            };
        });
    }
}
