using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using WinSMS.ViewModels;

namespace WinSMS.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        this.InitializeComponent();
    }

    private void ProfileColorSwatch_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Ellipse swatch ||
            swatch.DataContext is not string colorValue)
            return;

        swatch.Fill = new SolidColorBrush(ParseColor(colorValue));
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
}
