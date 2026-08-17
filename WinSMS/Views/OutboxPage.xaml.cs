using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using WinSMS.ViewModels;

namespace WinSMS.Views;

public sealed partial class OutboxPage : Page
{
    public OutboxViewModel ViewModel { get; }

    public OutboxPage()
    {
        ViewModel = App.Services.GetRequiredService<OutboxViewModel>();
        this.InitializeComponent();
    }
}
