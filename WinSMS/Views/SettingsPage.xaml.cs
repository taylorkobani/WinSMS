using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using WinSMS.ViewModels;

namespace WinSMS.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        var dataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinSMS");

        Directory.CreateDirectory(dataFolder);

        Process.Start(new ProcessStartInfo
        {
            FileName = dataFolder,
            UseShellExecute = true
        });
    }
}
