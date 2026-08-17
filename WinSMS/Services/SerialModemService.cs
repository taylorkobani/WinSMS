using System.IO.Ports;
using System.Text;
using Microsoft.Extensions.Logging;
using WinSMS.Models;
using WinSMS.Services.Interfaces;

namespace WinSMS.Services;

public class SerialModemService : IModemService, IDisposable
{
    private readonly ILogger<SerialModemService> _logger;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private SerialPort? _port;
    private ModemConnectionState _connectionState = ModemConnectionState.Disconnected;
    private ModemInfo? _modemInfo;
    private readonly StringBuilder _responseBuffer = new();
    private TaskCompletionSource<string>? _pendingResponse;
    private bool _disposed;

    public ModemConnectionState ConnectionState => _connectionState;
    public ModemInfo? ModemInfo => _modemInfo;

    public event EventHandler<ModemConnectionState>? ConnectionStateChanged;
    public event EventHandler<string>? UnsolicitedMessageReceived;

    public SerialModemService(ILogger<SerialModemService> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<string> GetAvailablePorts()
    {
        return SerialPort.GetPortNames();
    }

    public async Task ConnectAsync(string portName, int baudRate, CancellationToken cancellationToken = default)
    {
        if (_connectionState == ModemConnectionState.Connected)
            await DisconnectAsync();

        SetConnectionState(ModemConnectionState.Connecting);
        _logger.LogInformation("Connecting to modem on {PortName} at {BaudRate} baud", portName, baudRate);

        try
        {
            _port = new SerialPort(portName, baudRate)
            {
                ReadTimeout = 5000,
                WriteTimeout = 5000,
                NewLine = "\r\n",
                DtrEnable = true,
                RtsEnable = true
            };

            _port.DataReceived += OnDataReceived;
            _port.ErrorReceived += OnErrorReceived;
            _port.Open();

            SetConnectionState(ModemConnectionState.Connected);
            _logger.LogInformation("Connected to modem on {PortName}", portName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to modem on {PortName}", portName);
            SetConnectionState(ModemConnectionState.Error);
            throw;
        }

        await Task.CompletedTask;
    }

    public async Task DisconnectAsync()
    {
        _logger.LogInformation("Disconnecting modem");
        try
        {
            if (_port is { IsOpen: true })
            {
                _port.DataReceived -= OnDataReceived;
                _port.ErrorReceived -= OnErrorReceived;
                _port.Close();
            }
            _port?.Dispose();
            _port = null;
            _modemInfo = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during modem disconnect");
        }
        finally
        {
            SetConnectionState(ModemConnectionState.Disconnected);
        }

        await Task.CompletedTask;
    }

    public async Task<string> SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        if (_port is null || !_port.IsOpen)
            throw new InvalidOperationException("Modem is not connected.");

        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            _responseBuffer.Clear();
            _pendingResponse = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(5000));
            cts.Token.Register(() => _pendingResponse.TrySetCanceled());

            _logger.LogDebug("Sending AT command: {Command}", command);
            _port.WriteLine(command);

            var response = await _pendingResponse.Task;
            _logger.LogDebug("Received response for {Command}: {Response}", command, response);
            return response;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("AT command timed out: {Command}", command);
            throw;
        }
        finally
        {
            _pendingResponse = null;
            _commandLock.Release();
        }
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await SendCommandAsync("AT", cancellationToken);
            return response.Contains("OK", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Modem test failed");
            return false;
        }
    }

    public async Task<ModemInfo> GetModemInfoAsync(CancellationToken cancellationToken = default)
    {
        var info = new ModemInfo
        {
            PortName = _port?.PortName ?? string.Empty,
            BaudRate = _port?.BaudRate ?? 0
        };

        info.Manufacturer = await TryGetCommandValueAsync("AT+CGMI", cancellationToken);
        info.Model = await TryGetCommandValueAsync("AT+CGMM", cancellationToken)
                     ?? await TryGetCommandValueAsync("ATI", cancellationToken);
        info.SerialNumber = await TryGetCommandValueAsync("AT+CGSN", cancellationToken);

        _modemInfo = info;
        return info;
    }

    private async Task<string?> TryGetCommandValueAsync(string command, CancellationToken cancellationToken)
    {
        try
        {
            var response = await SendCommandAsync(command, cancellationToken);
            return ParseSingleLineResponse(response, command);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Command {Command} not supported or failed", command);
            return null;
        }
    }

    private static string? ParseSingleLineResponse(string response, string command)
    {
        var lines = response.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Equals("OK", StringComparison.OrdinalIgnoreCase)) continue;
            if (trimmed.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) return null;
            if (trimmed.StartsWith(command.TrimStart('A', 'T').TrimStart('+'), StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(trimmed))
                return trimmed;
        }
        return null;
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_port is null) return;
        try
        {
            var data = _port.ReadExisting();
            _responseBuffer.Append(data);
            var buffered = _responseBuffer.ToString();

            if (buffered.Contains("\r\nOK\r\n") || buffered.Contains("\r\nERROR\r\n")
                || buffered.Contains("+CME ERROR") || buffered.Contains("+CMS ERROR"))
            {
                _pendingResponse?.TrySetResult(buffered);
            }
            else if (_pendingResponse == null && (buffered.Contains("+CMTI:") || buffered.Contains("+CMT:")))
            {
                var notification = buffered.Trim();
                _responseBuffer.Clear();
                _logger.LogInformation("Unsolicited modem notification received");
                UnsolicitedMessageReceived?.Invoke(this, notification);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading serial port data");
            _pendingResponse?.TrySetException(ex);
        }
    }

    private void OnErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    {
        _logger.LogWarning("Serial port error: {EventType}", e.EventType);
        if (e.EventType == SerialError.Overrun || e.EventType == SerialError.TXFull)
        {
            SetConnectionState(ModemConnectionState.Error);
        }
    }

    private void SetConnectionState(ModemConnectionState state)
    {
        _connectionState = state;
        ConnectionStateChanged?.Invoke(this, state);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _port?.Dispose();
        _commandLock.Dispose();
    }
}
