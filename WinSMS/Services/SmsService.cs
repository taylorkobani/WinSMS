using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Windows.Devices.Sms;
using WinSMS.Models;
using WinSMS.Services.Interfaces;

namespace WinSMS.Services;

public class SmsService : ISmsService
{
    private readonly IModemService _modem;
    private readonly IMessageArchiveService _archive;
    private readonly ILogger<SmsService> _logger;
    private SmsMessageRegistration? _messageRegistration;

    public event EventHandler<SmsMessage>? MessageReceived;

    public SmsService(IModemService modem, IMessageArchiveService archive, ILogger<SmsService> logger)
    {
        _modem = modem;
        _archive = archive;
        _logger = logger;
        _modem.UnsolicitedMessageReceived += OnUnsolicitedMessageReceived;
        InitializeWindowsSmsReceiving();
    }

    public async Task<IReadOnlyList<SmsMessage>> GetAllMessagesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var archived = await _archive.LoadAllMessagesAsync();
        return archived.Where(m => m.Direction == SmsDirection.Incoming)
                       .OrderBy(m => m.Timestamp)
                       .ToList();
    }

    public async Task<IReadOnlyList<SmsMessage>> GetUnreadMessagesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var archived = await _archive.LoadAllMessagesAsync();
        return archived.Where(m => m.Direction == SmsDirection.Incoming && !m.IsRead)
                       .OrderBy(m => m.Timestamp)
                       .ToList();
    }

    public async Task<SmsMessage?> GetMessageByIndexAsync(int index, CancellationToken cancellationToken = default)
    {
        try
        {
            await _modem.SendCommandAsync("AT+CMGF=1", cancellationToken);
            return ParseCmgrResponse(await _modem.SendCommandAsync($"AT+CMGR={index}", cancellationToken), index);
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to retrieve SMS message at index {Index}", index); return null; }
    }

    public async Task<SmsMessage> SendMessageAsync(string phoneNumber, string body, CancellationToken cancellationToken = default)
    {
        var message = new SmsMessage
        {
            PhoneNumber = phoneNumber, Body = body, Direction = SmsDirection.Outgoing,
            Status = SmsStatus.Pending, Timestamp = DateTimeOffset.Now
        };
        await _archive.SaveMessageAsync(message);
        try
        {
            message.Status = SmsStatus.Sending;
            await _archive.UpdateMessageAsync(message);
            cancellationToken.ThrowIfCancellationRequested();

            var device = SmsDevice2.GetDefault()
                ?? throw new InvalidOperationException("Windows did not provide a default SMS device.");

            if (device.DeviceStatus != SmsDeviceStatus.Ready)
                throw new InvalidOperationException($"Windows SMS device is not ready. Status: {device.DeviceStatus}.");

            var sms = new SmsTextMessage2
            {
                To = phoneNumber,
                Body = body
            };

            var result = await device.SendMessageAndGetResultAsync(sms).AsTask(cancellationToken);
            if (result.IsSuccessful)
            {
                message.Status = SmsStatus.Sent;
                message.ModemReference = result.MessageReferenceNumbers.Count > 0
                    ? string.Join(",", result.MessageReferenceNumbers)
                    : null;
                _logger.LogInformation("SMS sent successfully through Windows SMS API to {PhoneNumber}", phoneNumber);
            }
            else
            {
                message.Status = SmsStatus.Failed;
                message.Error = $"Windows SMS send failed. CellularClass={result.CellularClass}; " +
                                $"ModemError={result.ModemErrorCode}; TransportFailure={result.TransportFailureCause}.";
                _logger.LogError("SMS send failed: {Error}", message.Error);
            }
        }
        catch (Exception ex)
        {
            message.Status = SmsStatus.Failed;
            message.Error = ex.Message;
            _logger.LogError(ex, "Failed to send SMS to {PhoneNumber}", phoneNumber);
        }
        finally { await _archive.UpdateMessageAsync(message); }
        return message;
    }

    public async Task DeleteMessageAsync(int modemIndex, CancellationToken cancellationToken = default)
    {
        try { await _modem.SendCommandAsync($"AT+CMGD={modemIndex}", cancellationToken); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to delete SMS at index {Index}", modemIndex); throw; }
    }

    public Task MarkAsReadAsync(SmsMessage message) { message.IsRead = true; return _archive.UpdateMessageAsync(message); }

    private void InitializeWindowsSmsReceiving()
    {
        const string registrationId = "WinSMS.TextMessages";
        string step = "starting";

        try
        {
            step = "reading SmsMessageRegistration.AllRegistrations";
            _logger.LogInformation("SMS registration diagnostic: {Step}", step);

            var registrations = SmsMessageRegistration.AllRegistrations;
            _logger.LogInformation(
                "SMS registration diagnostic: Windows returned {Count} registration(s).",
                registrations.Count);

            step = "looking for existing WinSMS registration";
            var existing = registrations.FirstOrDefault(r => r.Id == registrationId);

            if (existing != null)
            {
                _logger.LogInformation(
                    "SMS registration diagnostic: existing registration {RegistrationId} found.",
                    registrationId);
                _messageRegistration = existing;
            }
            else
            {
                step = "creating SMS filter rules";
                _logger.LogInformation("SMS registration diagnostic: {Step}", step);

                var rules = new SmsFilterRules(SmsFilterActionType.Accept);
                rules.Rules.Add(new SmsFilterRule(SmsMessageType.Text));

                step = "calling SmsMessageRegistration.Register";
                _logger.LogInformation(
                    "SMS registration diagnostic: registering {RegistrationId}.",
                    registrationId);

                _messageRegistration = SmsMessageRegistration.Register(registrationId, rules);

                _logger.LogInformation(
                    "SMS registration diagnostic: registration {RegistrationId} created.",
                    registrationId);
            }

            step = "detaching previous MessageReceived handler";
            _logger.LogInformation("SMS registration diagnostic: {Step}", step);
            _messageRegistration.MessageReceived -= OnWindowsSmsMessageReceived;

            step = "attaching MessageReceived handler";
            _logger.LogInformation("SMS registration diagnostic: {Step}", step);
            _messageRegistration.MessageReceived += OnWindowsSmsMessageReceived;

            _logger.LogInformation(
                "Windows SMS receive registration is active. RegistrationId={RegistrationId}",
                _messageRegistration.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to register for incoming Windows SMS messages. Step={Step}; " +
                "ExceptionType={ExceptionType}; HResult=0x{HResult:X8}; Message={Message}",
                step,
                ex.GetType().FullName,
                ex.HResult,
                ex.Message);

            System.Diagnostics.Debug.WriteLine(
                $"WinSMS SMS REGISTRATION FAILED | Step={step} | " +
                $"Type={ex.GetType().FullName} | HResult=0x{ex.HResult:X8} | " +
                $"Message={ex.Message}");
        }
    }

    private async void OnWindowsSmsMessageReceived(
        SmsMessageRegistration sender,
        SmsMessageReceivedTriggerDetails details)
    {
        try
        {
            if (details.MessageType != SmsMessageType.Text)
            {
                details.Accept();
                return;
            }

            var text = details.TextMessage;
            var message = new SmsMessage
            {
                PhoneNumber = text.From ?? string.Empty,
                Body = text.Body ?? string.Empty,
                Timestamp = text.Timestamp,
                Direction = SmsDirection.Incoming,
                Status = SmsStatus.Received,
                IsRead = false
            };

            // Acknowledge promptly so Windows can continue normal delivery,
            // including delivery to the system/operator messaging application.
            details.Accept();

            await _archive.SaveMessageAsync(message);
            _logger.LogInformation("Incoming SMS received from {PhoneNumber}", message.PhoneNumber);
            MessageReceived?.Invoke(this, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process an incoming Windows SMS message.");
            try { details.Accept(); } catch { }
        }
    }

    private async void OnUnsolicitedMessageReceived(object? sender, string notification)
    {
        var match = Regex.Match(notification, @"\+CMTI:\s*""?[^"",]+""?,\s*(\d+)");
        if (!match.Success) return;
        if (int.TryParse(match.Groups[1].Value, out var index))
        {
            try
            {
                var message = await GetMessageByIndexAsync(index);
                if (message != null) { await _archive.SaveMessageAsync(message); MessageReceived?.Invoke(this, message); }
            }
            catch (Exception ex) { _logger.LogError(ex, "Failed to retrieve new SMS at index {Index}", index); }
        }
    }

    internal static IReadOnlyList<SmsMessage> ParseCmglResponse(string response)
    {
        var messages = new List<SmsMessage>();
        var lines = response.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < lines.Length - 1; i++)
        {
            var headerMatch = Regex.Match(lines[i], @"\+CMGL:\s*(\d+),""([^""]+)"",""([^""]*)"",([^,]*),""([^""]*)""");
            if (headerMatch.Success && i + 1 < lines.Length)
            {
                var status = headerMatch.Groups[2].Value;
                var direction = InferDirection(status);
                messages.Add(new SmsMessage
                {
                    ModemMessageIndex = int.Parse(headerMatch.Groups[1].Value), PhoneNumber = headerMatch.Groups[3].Value,
                    Timestamp = ParseTimestamp(headerMatch.Groups[5].Value), Direction = direction,
                    Status = direction == SmsDirection.Incoming ? SmsStatus.Received : SmsStatus.Sent,
                    IsRead = !status.Contains("UNREAD", StringComparison.OrdinalIgnoreCase) && !status.Contains("UNSENT", StringComparison.OrdinalIgnoreCase),
                    Body = lines[i + 1].Trim()
                });
                i++;
            }
        }
        return messages;
    }

    private static SmsDirection InferDirection(string status) =>
        status.StartsWith("STO", StringComparison.OrdinalIgnoreCase) ? SmsDirection.Outgoing : SmsDirection.Incoming;

    internal static SmsMessage? ParseCmgrResponse(string response, int index)
    {
        var lines = response.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < lines.Length - 1; i++)
        {
            var m = Regex.Match(lines[i], @"\+CMGR:\s*""([^""]+)"",""([^""]*)"",([^,]*),""([^""]*)""");
            if (m.Success) return new SmsMessage
            {
                ModemMessageIndex = index, PhoneNumber = m.Groups[2].Value, Timestamp = ParseTimestamp(m.Groups[4].Value),
                Direction = SmsDirection.Incoming, Status = SmsStatus.Received,
                IsRead = !m.Groups[1].Value.Contains("UNREAD", StringComparison.OrdinalIgnoreCase),
                Body = i + 1 < lines.Length ? lines[i + 1].Trim() : string.Empty
            };
        }
        return null;
    }

    internal static DateTimeOffset ParseTimestamp(string timestamp)
    {
        if (string.IsNullOrWhiteSpace(timestamp)) return DateTimeOffset.Now;
        try
        {
            var m = Regex.Match(timestamp, @"(\d{2})/(\d{2})/(\d{2}),(\d{2}):(\d{2}):(\d{2})([+-]\d{2})");
            if (m.Success)
            {
                var offset = TimeSpan.FromMinutes(int.Parse(m.Groups[7].Value) * 15);
                return new DateTimeOffset(2000 + int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
                    int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value), int.Parse(m.Groups[6].Value), offset);
            }
        }
        catch { }
        return DateTimeOffset.Now;
    }

    private static string? ExtractCmgsReference(string response)
    {
        var m = Regex.Match(response, @"\+CMGS:\s*(\d+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    internal static string? ExtractError(string response)
    {
        var cme = Regex.Match(response, @"\+CME ERROR:\s*(.+)");
        if (cme.Success) return $"CME ERROR: {cme.Groups[1].Value.Trim()}";
        var cms = Regex.Match(response, @"\+CMS ERROR:\s*(.+)");
        if (cms.Success) return $"CMS ERROR: {cms.Groups[1].Value.Trim()}";
        return response.Contains("ERROR") ? "ERROR" : null;
    }
}
