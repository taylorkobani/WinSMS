using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinSMS.Services;
using WinSMS.Services.Interfaces;
using WinSMS.Views;

namespace WinSMS;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var phoneProfiles = App.Services.GetRequiredService<PhoneProfileService>();
        phoneProfiles.ProfilesChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdatePhoneProfile);
        UpdatePhoneProfile();
    }

    private void UpdatePhoneProfile()
    {
        var smsService = App.Services.GetRequiredService<ISmsService>();
        var phoneProfiles = App.Services.GetRequiredService<PhoneProfileService>();
        var localPhoneNumber = smsService.GetCurrentPhoneNumber();
        var profile = phoneProfiles.GetProfile(localPhoneNumber);

        // Keep the native window title stable; show the friendly account identity
        // as a colored pill in the visible app chrome.
        Title = "WinSMS";

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            PhoneProfilePill.Visibility = Visibility.Collapsed;
            return;
        }

        PhoneProfileName.Text = profile.Name;
        PhoneProfilePill.Background = new SolidColorBrush(ParseColor(profile.Color));
        PhoneProfilePill.Visibility = Visibility.Visible;
    }

    private static Color ParseColor(string value)
    {
        try
        {
            var hex = value.TrimStart('#');
            if (hex.Length == 6)
                return Color.FromArgb(
                    255,
                    Convert.ToByte(hex[0..2], 16),
                    Convert.ToByte(hex[2..4], 16),
                    Convert.ToByte(hex[4..6], 16));
        }
        catch { }

        return Color.FromArgb(255, 0, 120, 212);
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
