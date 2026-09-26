using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.Devices.Enumeration;
using Windows.Devices.Sms;
using Windows.Networking.NetworkOperators;

namespace WinSMS.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
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
                lines.Add($"Account number: {defaultDevice.AccountPhoneNumber ?? "(not reported)"}");
            }

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

                    try
                    {
                        var modem = MobileBroadbandModem.FromId(sms.ParentDeviceId);
                        if (modem != null)
                        {
                            lines.Add("MobileBroadbandModem.FromId: SUCCESS");
                            var deviceInfo = modem.DeviceInformation;
                            lines.Add($"Radio state: {deviceInfo.CurrentRadioState}");
                            lines.Add($"SIM ICCID: {deviceInfo.SimIccId ?? "(not reported)"}");
                            lines.Add($"Subscriber ID available: {!string.IsNullOrWhiteSpace(deviceInfo.SubscriberId)}");
                        }
                    }
                    catch (Exception ex)
                    {
                        lines.Add($"Mobile broadband access failed: {FormatException(ex)}");
                    }
                }
                catch (Exception ex)
                {
                    lines.Add($"SmsDevice2.FromId failed: {FormatException(ex)}");
                }
            }

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
