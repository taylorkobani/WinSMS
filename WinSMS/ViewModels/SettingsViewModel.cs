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
            lines.Add($"Mobile broadband telephone number(s): {(readyInfo.TelephoneNumbers.Count == 0 ? "(not reported)" : string.Join(", ", readyInfo.TelephoneNumbers))}");
            if (!string.IsNullOrWhiteSpace(readyInfo.Error))
                lines.Add($"Mobile broadband ready-info: {readyInfo.Error}");

            var effectiveNumber = await _smsService.SynchronizeCurrentPhoneNumberAsync();
            lines.Add($"WinSMS effective current number: {(string.IsNullOrWhiteSpace(effectiveNumber) ? "(not reported)" : effectiveNumber)}");

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

                try
                {
                    var sms = SmsDevice2.FromId(info.Id);
                    if (sms is null)
                    {
                        lines.Add("SmsDevice2.FromId: returned null");
                        continue;
                    }

                    lines.Add("SmsDevice2.FromId: SUCCESS");
                    lines.Add($"SMS status: {sms.DeviceStatus}");
                    lines.Add($"Cellular class: {sms.CellularClass}");
                    lines.Add($"Parent device ID: {sms.ParentDeviceId}");
                    lines.Add($"Account number: {sms.AccountPhoneNumber ?? "(not reported)"}");
                    lines.Add($"SMSC address: {sms.SmscAddress ?? "(not reported)"}");

                    // Do not call MobileBroadbandModem.FromId here. That API requires
                    // restricted cellular identity/control capabilities and can fail below
                    // the managed exception boundary on ordinary desktop installations.
                    // SmsDevice2 already exposes the information WinSMS is allowed to use.
                    lines.Add("Mobile broadband identity details: not queried (restricted Windows API).");
                }
                catch (Exception ex)
                {
                    lines.Add($"SmsDevice2.FromId failed: {FormatException(ex)}");
                }
            }

            var readyInfo = await _mobileBroadbandIdentity.GetReadyInfoAsync();
            lines.Add("");
            lines.Add("Mobile broadband ready-info:");
            lines.Add($"Telephone number(s): {(readyInfo.TelephoneNumbers.Count == 0 ? "(not reported)" : string.Join(", ", readyInfo.TelephoneNumbers))}");
            if (!string.IsNullOrWhiteSpace(readyInfo.Error))
                lines.Add($"Query status: {readyInfo.Error}");

            var effectiveNumber = await _smsService.SynchronizeCurrentPhoneNumberAsync();
            lines.Add($"WinSMS effective current number: {(string.IsNullOrWhiteSpace(effectiveNumber) ? "(not reported)" : effectiveNumber)}");

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
