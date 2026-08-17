using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using WinSMS.ViewModels;

namespace WinSMS.Views;

public sealed partial class ComposePage : Page
{
    public ComposeViewModel ViewModel { get; }

    public ComposePage()
    {
        ViewModel = App.Services.GetRequiredService<ComposeViewModel>();
        this.InitializeComponent();
    }
}
