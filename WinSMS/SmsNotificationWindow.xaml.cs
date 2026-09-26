using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace WinSMS;

public sealed partial class SmsNotificationWindow : Window
{
    private readonly AppWindow _appWindow;
    private string _phoneNumber = string.Empty;

    public event EventHandler<string>? NotificationClicked;

    public SmsNotificationWindow()
    {
        InitializeComponent();

        _appWindow = AppWindow;
        _appWindow.IsShownInSwitchers = false;

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        _appWindow.Resize(new global::Windows.Graphics.SizeInt32(390, 150));
        _appWindow.Hide();
    }

    public void ShowMessage(string phoneNumber, string body)
    {
        _phoneNumber = phoneNumber;
        SenderText.Text = phoneNumber;
        BodyText.Text = body;

        var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = _appWindow.Size;
        _appWindow.Move(new global::Windows.Graphics.PointInt32(
            area.X + area.Width - size.Width - 16,
            area.Y + area.Height - size.Height - 16));

        _appWindow.Show();
    }

    private void Notification_Click(object sender, RoutedEventArgs e)
    {
        _appWindow.Hide();
        NotificationClicked?.Invoke(this, _phoneNumber);
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e)
        => _appWindow.Hide();
}
