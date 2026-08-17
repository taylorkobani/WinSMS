using WinSMS.Models;

namespace WinSMS.Services.Interfaces;

public interface IModemService
{
    ModemConnectionState ConnectionState { get; }
    ModemInfo? ModemInfo { get; }

    event EventHandler<ModemConnectionState>? ConnectionStateChanged;
    event EventHandler<string>? UnsolicitedMessageReceived;

    IReadOnlyList<string> GetAvailablePorts();
    Task ConnectAsync(string portName, int baudRate, CancellationToken cancellationToken = default);
    Task DisconnectAsync();
    Task<string> SendCommandAsync(string command, CancellationToken cancellationToken = default);
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
    Task<ModemInfo> GetModemInfoAsync(CancellationToken cancellationToken = default);
}
