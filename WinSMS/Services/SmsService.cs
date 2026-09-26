using Microsoft.Extensions.Logging;
using Windows.Devices.Sms;
using WinSMS.Models;
using WinSMS.Services.Interfaces;

namespace WinSMS.Services;

public class SmsService : ISmsService
{
    private readonly IMessageArchiveService _archive;
    private readonly ILogger<SmsService> _logger;
    private SmsMessageRegistration? _messageRegistration;

    public event EventHandler<SmsMessage>? MessageReceived;

    public SmsService(IMessageArchiveService archive, ILogger<SmsService> logger)
    {
        _archive = archive;
        _logger = logger;
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

    public Task MarkAsReadAsync(SmsMessage message) { message.IsRead = true; return _archive.UpdateMessageAsync(message); }

    private void InitializeWindowsSmsReceiving()
    {
        const string registrationId = "WinSMS.TextMessages";

        try
        {
            // Do not reuse a registration left behind by a previous WinSMS process.
            // Windows can enumerate that registration, but subscribing to its
            // MessageReceived event can fail with 0xD000000D. Recreate it so the
            // event source belongs to this process.
            var existing = SmsMessageRegistration.AllRegistrations
                .FirstOrDefault(r => r.Id == registrationId);

            if (existing != null)
            {
                _logger.LogInformation(
                    "Removing stale SMS registration {RegistrationId} before re-registering.",
                    registrationId);
                existing.Unregister();
            }

            var rules = new SmsFilterRules(SmsFilterActionType.Accept);
            rules.Rules.Add(new SmsFilterRule(SmsMessageType.Text));

            _messageRegistration = SmsMessageRegistration.Register(registrationId, rules);
            _messageRegistration.MessageReceived += OnWindowsSmsMessageReceived;

            _logger.LogInformation(
                "Windows SMS receive registration is active. RegistrationId={RegistrationId}",
                _messageRegistration.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to register for incoming Windows SMS messages. " +
                "ExceptionType={ExceptionType}; HResult=0x{HResult:X8}; Message={Message}",
                ex.GetType().FullName,
                ex.HResult,
                ex.Message);

            System.Diagnostics.Debug.WriteLine(
                $"WinSMS SMS REGISTRATION FAILED | Type={ex.GetType().FullName} | " +
                $"HResult=0x{ex.HResult:X8} | Message={ex.Message}");
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


}
