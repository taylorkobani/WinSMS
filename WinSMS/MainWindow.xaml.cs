using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using System.Runtime.InteropServices;
using Windows.UI;
using WinSMS.Services;
using WinSMS.Services.Interfaces;
using WinSMS.ViewModels;
using WinSMS.Models;
using WinSMS.Views;

namespace WinSMS;

public sealed partial class MainWindow : Window
{
    private const uint WM_SIZE = 0x0005;
    private const uint WM_APP = 0x8000;
    private const uint WM_TRAYICON = WM_APP + 1;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const int SIZE_MINIMIZED = 1;
    private const int GWL_WNDPROC = -4;
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;
    private const uint NIIF_INFO = 0x00000001;
    private const uint NIN_BALLOONUSERCLICK = WM_APP + 5;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const uint NIM_SETVERSION = 0x00000004;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;

    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly WndProcDelegate _wndProc;
    private IntPtr _oldWndProc;
    private IntPtr _trayIconHandle;
    private bool _trayIconVisible;
    private string? _notificationPhoneNumber;
    private readonly SmsNotificationWindow _smsNotificationWindow;
    private bool _closeConfirmed;
    private bool _closeDialogOpen;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _smsNotificationWindow = new SmsNotificationWindow();
        _smsNotificationWindow.NotificationClicked += async (_, phoneNumber) =>
            await RestoreConversationAsync(phoneNumber);

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        _wndProc = WindowProc;
        _oldWndProc = SetWindowLongPtr(_hwnd, GWL_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_wndProc));
        _appWindow.Closing += AppWindow_Closing;
        Closed += MainWindow_Closed;

        var smsService = App.Services.GetRequiredService<ISmsService>();
        smsService.MessageReceived += OnSmsMessageReceived;

        var phoneProfiles = App.Services.GetRequiredService<PhoneProfileService>();
        phoneProfiles.ProfilesChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdatePhoneProfile);
        UpdatePhoneProfile();
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_SIZE && wParam.ToInt32() == SIZE_MINIMIZED)
        {
            DispatcherQueue.TryEnqueue(MinimizeToTray);
        }
        else if (msg == WM_TRAYICON)
        {
            var mouseMessage = unchecked((uint)lParam.ToInt64());
            // With NOTIFYICON_VERSION_4 the low word contains the notification code.
            mouseMessage &= 0xFFFF;

            if (mouseMessage == NIN_BALLOONUSERCLICK)
            {
                var phoneNumber = _notificationPhoneNumber;
                DispatcherQueue.TryEnqueue(async () => await RestoreConversationAsync(phoneNumber));
            }
            else if (mouseMessage == WM_LBUTTONUP || mouseMessage == WM_LBUTTONDBLCLK)
            {
                DispatcherQueue.TryEnqueue(RestoreFromTray);
            }
        }

        return CallWindowProc(_oldWndProc, hwnd, msg, wParam, lParam);
    }

    private void MinimizeToTray()
    {
        if (_trayIconVisible)
            return;

        AddTrayIcon();
        _appWindow.IsShownInSwitchers = false;
        _appWindow.Hide();
    }

    private void RestoreFromTray()
    {
        _appWindow.Show();
        _appWindow.IsShownInSwitchers = true;

        if (_appWindow.Presenter is OverlappedPresenter presenter)
            presenter.Restore();

        SetForegroundWindow(_hwnd);
        RemoveTrayIcon();
    }

    private void OnSmsMessageReceived(object? sender, SmsMessage message)
    {
        if (!_trayIconVisible)
            return;

        DispatcherQueue.TryEnqueue(() => ShowSmsBalloon(message));
    }

    private void ShowSmsBalloon(SmsMessage message)
    {
        if (!_trayIconVisible)
            return;

        _notificationPhoneNumber = message.PhoneNumber;

        _smsNotificationWindow.ShowMessage(message.PhoneNumber, message.Body);
    }

    private async Task RestoreConversationAsync(string? phoneNumber)
    {
        RestoreFromTray();

        var inboxItem = NavView.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), "Inbox", StringComparison.Ordinal));

        if (inboxItem != null)
            NavView.SelectedItem = inboxItem;

        if (ContentFrame.CurrentSourcePageType != typeof(InboxPage))
            ContentFrame.Navigate(typeof(InboxPage));

        if (!string.IsNullOrWhiteSpace(phoneNumber) &&
            ContentFrame.Content is InboxPage inboxPage)
        {
            await inboxPage.ViewModel.SelectConversationAsync(phoneNumber);
        }

        _notificationPhoneNumber = null;
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private void AddTrayIcon()
    {
        if (_trayIconVisible)
            return;

        _trayIconHandle = GetApplicationIcon();

        var data = CreateNotifyIconData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WM_TRAYICON;
        data.hIcon = _trayIconHandle;
        data.szTip = "WinSMS";

        if (Shell_NotifyIcon(NIM_ADD, ref data))
        {
            _trayIconVisible = true;
            data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIcon(NIM_SETVERSION, ref data);
        }
    }

    private void RemoveTrayIcon()
    {
        if (!_trayIconVisible)
            return;

        var data = CreateNotifyIconData();
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _trayIconVisible = false;

        if (_trayIconHandle != IntPtr.Zero)
        {
            DestroyIcon(_trayIconHandle);
            _trayIconHandle = IntPtr.Zero;
        }
    }

    private NOTIFYICONDATA CreateNotifyIconData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1
    };

    private static IntPtr GetApplicationIcon()
    {
        var executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable))
        {
            var icons = new IntPtr[1];
            if (ExtractIconEx(executable, 0, icons, null, 1) > 0 &&
                icons[0] != IntPtr.Zero)
                return icons[0];
        }

        return LoadIcon(IntPtr.Zero, new IntPtr(32512));
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed)
            return;

        // The title-bar Close button behaves like Minimize: keep WinSMS
        // running in the notification area instead of terminating it.
        args.Cancel = true;
        MinimizeToTray();
    }

    private async void ExitNavigationItem_Tapped(
        object sender,
        Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (_closeDialogOpen)
            return;

        _closeDialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Exit WinSMS?",
                Content = "Are you sure you want to exit WinSMS?",
                PrimaryButtonText = "Exit",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                _closeConfirmed = true;
                Close();
            }
        }
        finally
        {
            _closeDialogOpen = false;
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _appWindow.Closing -= AppWindow_Closing;
        App.Services.GetRequiredService<ISmsService>().MessageReceived -= OnSmsMessageReceived;
        RemoveTrayIcon();

        // The SMS popup is a second top-level WinUI Window. Hiding it is not
        // enough: if it remains alive after MainWindow closes, the process and
        // Visual Studio debugging session remain active.
        _smsNotificationWindow.Close();

        if (_oldWndProc != IntPtr.Zero)
        {
            SetWindowLongPtr(_hwnd, GWL_WNDPROC, _oldWndProc);
            _oldWndProc = IntPtr.Zero;
        }
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string szFileName, int nIconIndex, IntPtr[]? phiconLarge,
        IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(
        IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(
        IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private void UpdatePhoneProfile()
    {
        var smsService = App.Services.GetRequiredService<ISmsService>();
        var phoneProfiles = App.Services.GetRequiredService<PhoneProfileService>();
        var localPhoneNumber = smsService.GetCurrentPhoneNumber();
        var profile = phoneProfiles.GetProfile(localPhoneNumber);

        // Keep the native window title stable; show the friendly account identity
        // as a colored pill in the visible app chrome.
        Title = "WinSMS";

        var displayName = string.IsNullOrWhiteSpace(profile.Name)
            ? localPhoneNumber
            : profile.Name;

        if (string.IsNullOrWhiteSpace(displayName))
        {
            PhoneProfilePill.Visibility = Visibility.Collapsed;
            return;
        }

        PhoneProfileName.Text = displayName;
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
                "Profiles" => typeof(ProfilesPage),
                _ => typeof(InboxPage)
            };
            ContentFrame.Navigate(page);
        }
    }
}
