using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using WinSMS.Models;
using WinSMS.Services;
using WinSMS.Services.Interfaces;

namespace WinSMS.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IModemService _modemService;
    private readonly AppSettings _settings;
    private readonly DispatcherQueue _dispatcher;

    [ObservableProperty]
    private ObservableCollection<string> _availablePorts = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string _selectedPort = string.Empty;

    [ObservableProperty]
    private int _baudRate = 115200;

    [ObservableProperty]
    private int _commandTimeoutMs = 5000;

    [ObservableProperty]
    private bool _autoConnect;

    [ObservableProperty]
    private int _pollingIntervalSeconds = 30;

    [ObservableProperty]
    private ModemConnectionState _connectionState = ModemConnectionState.Disconnected;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _modemManufacturer;

    [ObservableProperty]
    private string? _modemModel;

    [ObservableProperty]
    private string? _modemSerial;

    public ObservableCollection<int> BaudRates { get; } = new(new[] { 9600, 19200, 38400, 57600, 115200, 230400 });

    public SettingsViewModel(IModemService modemService, AppSettings settings)
    {
        _modemService = modemService;
        _settings = settings;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _modemService.ConnectionStateChanged += OnConnectionStateChanged;
        ConnectionState = _modemService.ConnectionState;

        LoadFromSettings();
        RefreshPortsCommand.Execute(null);
    }

    private void LoadFromSettings()
    {
        SelectedPort = _settings.SelectedPort;
        BaudRate = _settings.BaudRate;
        CommandTimeoutMs = _settings.CommandTimeoutMs;
        AutoConnect = _settings.AutoConnect;
        PollingIntervalSeconds = _settings.PollingIntervalSeconds;
    }

    private void SaveToSettings()
    {
        _settings.SelectedPort = SelectedPort;
        _settings.BaudRate = BaudRate;
        _settings.CommandTimeoutMs = CommandTimeoutMs;
        _settings.AutoConnect = AutoConnect;
        _settings.PollingIntervalSeconds = PollingIntervalSeconds;
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        AvailablePorts.Clear();
        foreach (var port in _modemService.GetAvailablePorts())
            AvailablePorts.Add(port);
    }

    private bool CanConnect => !string.IsNullOrWhiteSpace(SelectedPort)
        && ConnectionState != ModemConnectionState.Connected
        && ConnectionState != ModemConnectionState.Connecting;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        StatusMessage = null;
        try
        {
            SaveToSettings();
            await _modemService.ConnectAsync(SelectedPort, BaudRate);
            StatusMessage = "Connected successfully.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Connection failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await _modemService.DisconnectAsync();
        StatusMessage = "Disconnected.";
        ClearModemInfo();
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        StatusMessage = "Testing connection...";
        try
        {
            var ok = await _modemService.TestConnectionAsync();
            StatusMessage = ok ? "Modem responded OK." : "Modem did not respond with OK.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Test failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task GetModemInfoAsync()
    {
        StatusMessage = "Retrieving modem information...";
        try
        {
            var info = await _modemService.GetModemInfoAsync();
            ModemManufacturer = info.Manufacturer ?? "Unknown";
            ModemModel = info.Model ?? "Unknown";
            ModemSerial = info.SerialNumber ?? "Unknown";
            StatusMessage = "Modem information retrieved.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to get modem info: {ex.Message}";
        }
    }

    private void OnConnectionStateChanged(object? sender, ModemConnectionState state)
    {
        _dispatcher.TryEnqueue(() =>
        {
            ConnectionState = state;
            ConnectCommand.NotifyCanExecuteChanged();
        });
    }

    private void ClearModemInfo()
    {
        ModemManufacturer = null;
        ModemModel = null;
        ModemSerial = null;
    }
}
