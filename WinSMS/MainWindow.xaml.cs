using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using WinSMS.Services.Interfaces;
using WinSMS.Views;

namespace WinSMS;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        UpdateWindowTitle();
    }

    private void UpdateWindowTitle()
    {
        var smsService = App.Services.GetRequiredService<ISmsService>();
        var localPhoneNumber = smsService.GetCurrentPhoneNumber();
        Title = string.IsNullOrWhiteSpace(localPhoneNumber)
            ? "WinSMS"
            : $"WinSMS - {localPhoneNumber}";
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
            var page = item.Tag?.ToString() switch
            {
                "Inbox" => typeof(InboxPage),
                "Compose" => typeof(ComposePage),
                _ => typeof(InboxPage)
            };
            ContentFrame.Navigate(page);
        }
    }
}
