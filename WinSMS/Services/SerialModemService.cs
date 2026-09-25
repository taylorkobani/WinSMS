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
    private ResponseMode _responseMode = ResponseMode.Command;
    private bool _disposed;

    private enum ResponseMode { Command, SmsPrompt, SmsResult }

    public ModemConnectionState ConnectionState => _connectionState;
    public ModemInfo? ModemInfo => _modemInfo;

    public event EventHandler<ModemConnectionState>? ConnectionStateChanged;
    public event EventHandler<string>? UnsolicitedMessageReceived;

    public SerialModemService(ILogger<SerialModemService> logger) => _logger = logger;

    public IReadOnlyList<string> GetAvailablePorts() => SerialPort.GetPortNames();

    public async Task ConnectAsync(string portName, int baudRate, CancellationToken cancellationToken = default)
    {
        if (_connectionState == ModemConnectionState.Connected) await DisconnectAsync();
        SetConnectionState(ModemConnectionState.Connecting);
        _logger.LogInformation("Connecting to modem on {PortName} at {BaudRate} baud", portName, baudRate);
        try
        {
            _port = new SerialPort(portName, baudRate)
            {
                ReadTimeout = 5000, WriteTimeout = 5000, NewLine = "\r\n",
                DtrEnable = true, RtsEnable = true
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
        catch (Exception ex) { _logger.LogWarning(ex, "Error during modem disconnect"); }
        finally { SetConnectionState(ModemConnectionState.Disconnected); }
        await Task.CompletedTask;
    }

    public async Task<string> SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            PrepareResponse(ResponseMode.Command);
            using var cts = CreateTimeout(cancellationToken, TimeSpan.FromSeconds(5));
            using var registration = cts.Token.Register(() => _pendingResponse?.TrySetCanceled(cts.Token));
            _logger.LogInformation("SERIAL TX [{Port}]: {Data}", _port!.PortName, EscapeForLog(command + "\\r\\n"));
            _port!.WriteLine(command);
            var response = await _pendingResponse!.Task;
            _logger.LogDebug("Received response for {Command}: {Response}", command, response);
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("AT command timed out: {Command}. Buffered RX: {Buffered}", command, EscapeForLog(_responseBuffer.ToString()));
            throw new TimeoutException($"AT command timed out: {command}");
        }
        finally { ClearPendingResponse(); _commandLock.Release(); }
    }

    public async Task<string> SendSmsAsync(string phoneNumber, string body, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            // CMGS is interactive: first wait for the '>' prompt, then send body + Ctrl+Z.
            PrepareResponse(ResponseMode.SmsPrompt);
            using (var promptCts = CreateTimeout(cancellationToken, TimeSpan.FromSeconds(10)))
            using (promptCts.Token.Register(() => _pendingResponse?.TrySetCanceled(promptCts.Token)))
            {
                _logger.LogInformation("SERIAL TX [{Port}]: {Data}", _port!.PortName, EscapeForLog($"AT+CMGS=\\\"{phoneNumber}\\\"\\r\\n"));
                _port!.WriteLine($"AT+CMGS=\"{phoneNumber}\"");
                await _pendingResponse!.Task;
            }

            PrepareResponse(ResponseMode.SmsResult);
            using (var resultCts = CreateTimeout(cancellationToken, TimeSpan.FromSeconds(60)))
            using (resultCts.Token.Register(() => _pendingResponse?.TrySetCanceled(resultCts.Token)))
            {
                _port!.Write(body);
                _port.Write(new[] { (char)0x1A }, 0, 1);
                var response = await _pendingResponse!.Task;
                _logger.LogDebug("SMS submission response: {Response}", response);
                return response;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("SMS submission timed out for {PhoneNumber}. Mode: {Mode}. Buffered RX: {Buffered}", phoneNumber, _responseMode, EscapeForLog(_responseBuffer.ToString()));
            throw new TimeoutException("The modem timed out while sending the SMS.");
        }
        finally { ClearPendingResponse(); _commandLock.Release(); }
    }

    private void EnsureConnected()
    {
        if (_port is null || !_port.IsOpen) throw new InvalidOperationException("Modem is not connected.");
    }

    private static CancellationTokenSource CreateTimeout(CancellationToken token, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout);
        return cts;
    }

    private void PrepareResponse(ResponseMode mode)
    {
        _responseBuffer.Clear();
        _responseMode = mode;
        _pendingResponse = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void ClearPendingResponse()
    {
        _pendingResponse = null;
        _responseMode = ResponseMode.Command;
        _responseBuffer.Clear();
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try { return (await SendCommandAsync("AT", cancellationToken)).Contains("OK", StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) { _logger.LogWarning(ex, "Modem test failed"); return false; }
    }

    public async Task<ModemInfo> GetModemInfoAsync(CancellationToken cancellationToken = default)
    {
        var info = new ModemInfo { PortName = _port?.PortName ?? string.Empty, BaudRate = _port?.BaudRate ?? 0 };
        info.Manufacturer = await TryGetCommandValueAsync("AT+CGMI", cancellationToken);
        info.Model = await TryGetCommandValueAsync("AT+CGMM", cancellationToken) ?? await TryGetCommandValueAsync("ATI", cancellationToken);
        info.SerialNumber = await TryGetCommandValueAsync("AT+CGSN", cancellationToken);
        _modemInfo = info;
        return info;
    }

    private async Task<string?> TryGetCommandValueAsync(string command, CancellationToken cancellationToken)
    {
        try { return ParseSingleLineResponse(await SendCommandAsync(command, cancellationToken), command); }
        catch (Exception ex) { _logger.LogDebug(ex, "Command {Command} not supported or failed", command); return null; }
    }

    private static string? ParseSingleLineResponse(string response, string command)
    {
        foreach (var line in response.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Equals("OK", StringComparison.OrdinalIgnoreCase)) continue;
            if (trimmed.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) return null;
            if (trimmed.StartsWith(command.TrimStart('A', 'T').TrimStart('+'), StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(trimmed)) return trimmed;
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

            var hasError = buffered.Contains("\r\nERROR\r\n", StringComparison.OrdinalIgnoreCase)
                || buffered.Contains("+CME ERROR", StringComparison.OrdinalIgnoreCase)
                || buffered.Contains("+CMS ERROR", StringComparison.OrdinalIgnoreCase);
            var complete = _responseMode switch
            {
                ResponseMode.SmsPrompt => hasError || buffered.Contains('>'),
                ResponseMode.SmsResult => hasError || buffered.Contains("\r\nOK\r\n", StringComparison.OrdinalIgnoreCase),
                _ => hasError || buffered.Contains("\r\nOK\r\n", StringComparison.OrdinalIgnoreCase)
            };

            if (_pendingResponse != null && complete)
                _pendingResponse.TrySetResult(buffered);
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

    private static string EscapeForLog(string value)\n    {\n        return value.Replace("\\r", "<CR>").Replace("\\n", "<LF>").Replace("\\t", "<TAB>");\n    }\n\n    private void OnErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    {
        _logger.LogWarning("Serial port error: {EventType}", e.EventType);
        if (e.EventType == SerialError.Overrun || e.EventType == SerialError.TXFull) SetConnectionState(ModemConnectionState.Error);
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
