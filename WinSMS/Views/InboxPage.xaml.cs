using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using WinSMS.ViewModels;

namespace WinSMS.Views;

public sealed partial class InboxPage : Page
{
    public InboxViewModel ViewModel { get; }

    public InboxPage()
    {
        ViewModel = App.Services.GetRequiredService<InboxViewModel>();
        this.InitializeComponent();
    }
}
