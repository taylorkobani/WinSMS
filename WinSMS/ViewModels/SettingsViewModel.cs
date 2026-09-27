using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.Devices.Enumeration;
using Windows.Devices.Sms;
using WinSMS.Services;
using WinSMS.Services.Interfaces;

namespace WinSMS.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly StartupService _startupService;
    private readonly MobileBroadbandIdentityService _mobileBroadbandIdentity;
    private readonly ISmsService _smsService;
    private bool _updatingStartup;

    [ObservableProperty]
    private bool _runAtWindowsStartup;

    public SettingsViewModel(
        StartupService startupService,
        MobileBroadbandIdentityService mobileBroadbandIdentity,
        ISmsService smsService)
    {
        _startupService = startupService;
        _mobileBroadbandIdentity = mobileBroadbandIdentity;
        _smsService = smsService;
        _runAtWindowsStartup = _startupService.IsEnabled();
    }

    partial void OnRunAtWindowsStartupChanged(bool value)
    {
        if (_updatingStartup) return;

        try
        {
            _startupService.SetEnabled(value);
            StatusMessage = value
                ? "WinSMS will start automatically when you sign in to Windows."
                : "WinSMS will no longer start automatically with Windows.";
        }
        catch (Exception ex)
        {
            _updatingStartup = true;
            RunAtWindowsStartup = _startupService.IsEnabled();
            _updatingStartup = false;
            StatusMessage = $"Could not change Windows startup setting: {ex.Message}";
        }
    }

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string _windowsSmsDiagnostics = "Not checked.";

    [RelayCommand]
    private async Task DetectWindowsSmsAsync()
    {
        StatusMessage = "Checking Windows SMS devices...";
        try
        {
            var selector = SmsDevice2.GetDeviceSelector();
            var devices = await DeviceInformation.FindAllAsync(selector);
            var lines = new List<string> { $"SMS devices found: {devices.Count}" };

            foreach (var device in devices)
                lines.Add($"- {device.Name} | Id: {device.Id}");

            var defaultDevice = SmsDevice2.GetDefault();
            if (defaultDevice is null)
            {
                lines.Add("Default SMS device: none");
            }
            else
            {
                lines.Add($"Default device ID: {defaultDevice.DeviceId}");
                lines.Add($"Parent device ID: {defaultDevice.ParentDeviceId}");
                lines.Add($"Status: {defaultDevice.DeviceStatus}");
                lines.Add($"Cellular class: {defaultDevice.CellularClass}");
                lines.Add($"SmsDevice2 account number: {defaultDevice.AccountPhoneNumber ?? "(not reported)"}");
            }

            var readyInfo = await _mobileBroadbandIdentity.GetReadyInfoAsync();
            lines.Add($"Mobile broadband interface(s): {(readyInfo.InterfaceNames.Count == 0 ? "(not reported)" : string.Join(", ", readyInfo.InterfaceNames))}");

            var legacyNumbers = readyInfo.LegacySubscribers
                .SelectMany(subscriber => subscriber.TelephoneNumbers)
                .Where(number => !string.IsNullOrWhiteSpace(number))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            lines.Add($"Win32 MBN subscriber number(s): {(legacyNumbers.Count == 0 ? "(not reported)" : string.Join(", ", legacyNumbers))}");

            if (readyInfo.SelectedSlot != null)
            {
                lines.Add($"Selected SIM slot: {readyInfo.SelectedSlot.SlotIndex}" +
                          (readyInfo.SelectedSlot.IsEsim ? " (eSIM)" : string.Empty));
                lines.Add($"Selected slot state: {(string.IsNullOrWhiteSpace(readyInfo.SelectedSlot.State) ? "(not reported)" : readyInfo.SelectedSlot.State)}");
            }
            else
            {
                lines.Add("Selected SIM slot: (not reported)");
            }

            lines.Add($"Mobile broadband telephone number(s): {(readyInfo.TelephoneNumbers.Count == 0 ? "(not reported)" : string.Join(", ", readyInfo.TelephoneNumbers))}");
            if (!string.IsNullOrWhiteSpace(readyInfo.Error))
                lines.Add($"Mobile broadband ready-info: {readyInfo.Error}");

            var subscription = await _smsService.SynchronizeCurrentSubscriptionAsync();
            lines.Add($"WinSMS current ICCID: {(string.IsNullOrWhiteSpace(subscription?.IccId) ? "(not reported)" : subscription.IccId)}");
            lines.Add($"WinSMS SIM type: {subscription?.SimTypeLabel ?? "(not reported)"}");
            lines.Add($"Windows profile name: {(string.IsNullOrWhiteSpace(subscription?.WindowsProfileName) ? "(not reported)" : subscription.WindowsProfileName)}");
            lines.Add($"Windows subscriber phone number: {(string.IsNullOrWhiteSpace(subscription?.WindowsPhoneNumber) ? "(not reported)" : subscription.WindowsPhoneNumber)}");

            WindowsSmsDiagnostics = string.Join(Environment.NewLine, lines);
            StatusMessage = devices.Count > 0
                ? "Windows SMS detection completed."
                : "Windows did not enumerate an accessible SMS device.";
        }
        catch (Exception ex)
        {
            WindowsSmsDiagnostics = FormatException(ex);
            StatusMessage = "Windows SMS detection failed.";
        }
    }

    [RelayCommand]
    private async Task DiagnoseWindowsSmsAsync()
    {
        StatusMessage = "Running Windows SMS diagnostics...";
        var lines = new List<string>();

        try
        {
            var selector = SmsDevice2.GetDeviceSelector();
            var devices = await DeviceInformation.FindAllAsync(selector);
            lines.Add($"SMS devices enumerated: {devices.Count}");

            foreach (var info in devices)
            {
                lines.Add($"Device: {info.Name}");
                lines.Add($"Device ID: {info.Id}");
                lines.Add($"PnP enabled: {info.IsEnabled}");
            }

            var defaultDevice = SmsDevice2.GetDefault();
            if (defaultDevice == null)
            {
                lines.Add("Default SMS device: none");
            }
            else
            {
                lines.Add("Default SMS device:");
                lines.Add($"SMS status: {defaultDevice.DeviceStatus}");
                lines.Add($"Cellular class: {defaultDevice.CellularClass}");
                lines.Add($"Parent device ID: {defaultDevice.ParentDeviceId}");
                lines.Add($"SmsDevice2 account number: {defaultDevice.AccountPhoneNumber ?? "(not reported)"}");
                lines.Add($"SMSC address: {defaultDevice.SmscAddress ?? "(not reported)"}");
            }

            var readyInfo = await _mobileBroadbandIdentity.GetReadyInfoAsync();
            lines.Add("");
            lines.Add("Mobile broadband ready-info:");
            lines.Add($"Interface(s): {(readyInfo.InterfaceNames.Count == 0 ? "(not reported)" : string.Join(", ", readyInfo.InterfaceNames))}");

            lines.Add("Win32 MBN subscriber data:");
            if (readyInfo.LegacySubscribers.Count == 0)
            {
                lines.Add("- (not available)");
            }
            else
            {
                foreach (var subscriber in readyInfo.LegacySubscribers)
                {
                    lines.Add($"- Interface ID: {(string.IsNullOrWhiteSpace(subscriber.InterfaceId) ? "(not reported)" : subscriber.InterfaceId)}");
                    lines.Add($"  ICCID: {(string.IsNullOrWhiteSpace(subscriber.SimIccId) ? "(not reported)" : subscriber.SimIccId)}");
                    lines.Add($"  Subscriber ID: {(string.IsNullOrWhiteSpace(subscriber.SubscriberId) ? "(not reported)" : subscriber.SubscriberId)}");
                    lines.Add($"  Telephone number(s): {(subscriber.TelephoneNumbers.Count == 0 ? "(not reported)" : string.Join(", ", subscriber.TelephoneNumbers))}");
                    if (!string.IsNullOrWhiteSpace(subscriber.Error))
                        lines.Add($"  Error: {subscriber.Error}");
                }
            }

            lines.Add($"Selected SIM slot: {(readyInfo.SelectedSlot == null ? "(not reported)" : readyInfo.SelectedSlot.SlotIndex.ToString())}");
            if (readyInfo.SelectedSlot != null)
            {
                lines.Add($"Selected slot type: {(readyInfo.SelectedSlot.IsEsim ? "eSIM" : "physical/unknown")}");
                lines.Add($"Selected slot state: {(string.IsNullOrWhiteSpace(readyInfo.SelectedSlot.State) ? "(not reported)" : readyInfo.SelectedSlot.State)}");
                lines.Add($"Selected slot telephone number(s): {(readyInfo.SelectedSlot.TelephoneNumbers.Count == 0 ? "(not reported)" : string.Join(", ", readyInfo.SelectedSlot.TelephoneNumbers))}");
            }

            lines.Add("Slot probes:");
            if (readyInfo.Slots.Count == 0)
            {
                lines.Add("- (none reported)");
            }
            else
            {
                foreach (var slot in readyInfo.Slots)
                {
                    lines.Add($"- Slot {slot.SlotIndex}: selected={slot.IsSelected}, eSIM={slot.IsEsim}, readyinfo exit={slot.ReadyInfoExitCode}");
                    lines.Add($"  State: {(string.IsNullOrWhiteSpace(slot.State) ? "(not reported)" : slot.State)}");
                    lines.Add($"  Telephone number(s): {(slot.TelephoneNumbers.Count == 0 ? "(not reported)" : string.Join(", ", slot.TelephoneNumbers))}");
                    if (!string.IsNullOrWhiteSpace(slot.ReadyInfoError))
                        lines.Add($"  Error: {slot.ReadyInfoError}");
                }
            }

            lines.Add($"Telephone number(s) selected by WinSMS identity resolver: {(readyInfo.TelephoneNumbers.Count == 0 ? "(not reported)" : string.Join(", ", readyInfo.TelephoneNumbers))}");
            if (!string.IsNullOrWhiteSpace(readyInfo.Error))
                lines.Add($"Query status: {readyInfo.Error}");

            var subscription = await _smsService.SynchronizeCurrentSubscriptionAsync();
            lines.Add($"WinSMS current ICCID: {(string.IsNullOrWhiteSpace(subscription?.IccId) ? "(not reported)" : subscription.IccId)}");
            lines.Add($"WinSMS SIM type: {subscription?.SimTypeLabel ?? "(not reported)"}");
            lines.Add($"Windows profile name: {(string.IsNullOrWhiteSpace(subscription?.WindowsProfileName) ? "(not reported)" : subscription.WindowsProfileName)}");
            lines.Add($"Windows subscriber phone number: {(string.IsNullOrWhiteSpace(subscription?.WindowsPhoneNumber) ? "(not reported)" : subscription.WindowsPhoneNumber)}");

            StatusMessage = devices.Count > 0
                ? "Windows SMS diagnostics completed."
                : "No Windows SMS device found.";
        }
        catch (Exception ex)
        {
            lines.Add($"SMS enumeration failed: {FormatException(ex)}");
            StatusMessage = "Windows SMS diagnostics failed.";
        }

        WindowsSmsDiagnostics = string.Join(Environment.NewLine, lines);
    }

    private static string FormatException(Exception ex) =>
        $"{ex.GetType().Name}, HRESULT 0x{ex.HResult:X8}: {ex.Message}";
}
