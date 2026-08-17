using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using WinSMS.Models;
using WinSMS.Services;
using WinSMS.Services.Interfaces;
using WinSMS.ViewModels;
using WinSMS.Views;

namespace WinSMS;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    private MainWindow? _mainWindow;

    public App()
    {
        this.InitializeComponent();
        Services = ConfigureServices();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mainWindow = new MainWindow();
        _mainWindow.Activate();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Settings singleton
        services.AddSingleton<AppSettings>();

        // Services
        services.AddSingleton<IModemService, SerialModemService>();
        services.AddSingleton<IMessageArchiveService, XmlMessageArchiveService>();
        services.AddSingleton<ISmsService, SmsService>();

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<InboxViewModel>();
        services.AddTransient<OutboxViewModel>();
        services.AddTransient<ComposeViewModel>();
        services.AddTransient<SettingsViewModel>();

        // Logging
        services.AddLogging(builder =>
        {
            builder.AddDebug();
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        return services.BuildServiceProvider();
    }
}
