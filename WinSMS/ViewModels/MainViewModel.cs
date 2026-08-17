using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using WinSMS.Models;
using WinSMS.Services.Interfaces;

namespace WinSMS.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IModemService _modemService;
    private readonly DispatcherQueue _dispatcher;

    [ObservableProperty]
    private ModemConnectionState _connectionState = ModemConnectionState.Disconnected;

    [ObservableProperty]
    private string _connectionStateText = "Disconnected";

    public MainViewModel(IModemService modemService)
    {
        _modemService = modemService;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _modemService.ConnectionStateChanged += OnConnectionStateChanged;
        ConnectionState = _modemService.ConnectionState;
        UpdateConnectionStateText();
    }

    private void OnConnectionStateChanged(object? sender, ModemConnectionState state)
    {
        _dispatcher.TryEnqueue(() =>
        {
            ConnectionState = state;
            UpdateConnectionStateText();
        });
    }

    private void UpdateConnectionStateText()
    {
        ConnectionStateText = ConnectionState switch
        {
            ModemConnectionState.Connected => "Connected",
            ModemConnectionState.Connecting => "Connecting...",
            ModemConnectionState.Error => "Error",
            _ => "Disconnected"
        };
    }
}
