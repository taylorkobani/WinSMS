using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using WinSMS.ViewModels;

namespace WinSMS.Views;

public sealed partial class ProfilesPage : Page
{
    public ProfilesViewModel ViewModel { get; }

    public ProfilesPage()
    {
        ViewModel = App.Services.GetRequiredService<ProfilesViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
    }

    private async void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PhoneProfileItem profile })
            await ViewModel.SaveProfileCommand.ExecuteAsync(profile);
    }

    private void ProfileColorSwatch_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Ellipse swatch || swatch.DataContext is not string colorValue)
            return;

        swatch.Fill = new SolidColorBrush(ParseColor(colorValue));
    }

    private static Color ParseColor(string value)
    {
        try
        {
            var hex = value.TrimStart('#');
            if (hex.Length == 6)
                return Color.FromArgb(255,
                    Convert.ToByte(hex[0..2], 16),
                    Convert.ToByte(hex[2..4], 16),
                    Convert.ToByte(hex[4..6], 16));
        }
        catch { }

        return Color.FromArgb(255, 0, 120, 212);
    }
}
