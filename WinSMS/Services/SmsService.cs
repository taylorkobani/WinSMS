using Microsoft.Extensions.Logging;
using Windows.Devices.Sms;
using WinSMS.Models;
using WinSMS.Services.Interfaces;

namespace WinSMS.Services;

public class SmsService : ISmsService
{
    private readonly IMessageArchiveService _archive;
    private readonly ILogger<SmsService> _logger;
    private readonly BlockedNumberService _blockedNumbers;
    private SmsMessageRegistration? _messageRegistration;
    private readonly object _phoneNumberSync = new();
    private string _currentPhoneNumber = string.Empty;

    public event EventHandler<SmsMessage>? MessageReceived;
    public event EventHandler<string>? CurrentPhoneNumberChanged;

    public string GetCurrentPhoneNumber()
    {
        var number = SmsDevice2.GetDefault()?.AccountPhoneNumber?.Trim() ?? string.Empty;
        UpdateCurrentPhoneNumber(number);
        return number;
    }

    public Task<string> SynchronizeCurrentPhoneNumberAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Deliberately use only the same default SmsDevice2 access path that
        // WinSMS uses for sending. This method is called from the UI thread.
        // Avoid device enumeration/FromId and mobile-broadband identity APIs,
        // which proved disruptive on real modem hardware.
        return Task.FromResult(GetCurrentPhoneNumber());
    }

    public SmsService(IMessageArchiveService archive, ILogger<SmsService> logger, BlockedNumberService blockedNumbers)
    {
        _archive = archive;
        _logger = logger;
        _blockedNumbers = blockedNumbers;
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
        var device = SmsDevice2.GetDefault()
            ?? throw new InvalidOperationException("Windows did not provide a default SMS device.");

        UpdateCurrentPhoneNumber(device.AccountPhoneNumber);

        var message = new SmsMessage
        {
            PhoneNumber = phoneNumber,
            LocalPhoneNumber = device.AccountPhoneNumber?.Trim() ?? string.Empty,
            Body = body,
            Direction = SmsDirection.Outgoing,
            Status = SmsStatus.Pending,
            Timestamp = DateTimeOffset.Now
        };
        await _archive.SaveMessageAsync(message);
        try
        {
            message.Status = SmsStatus.Sending;
            await _archive.UpdateMessageAsync(message);
            cancellationToken.ThrowIfCancellationRequested();

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

    private void UpdateCurrentPhoneNumber(string? phoneNumber)
    {
        var value = phoneNumber?.Trim() ?? string.Empty;
        var changed = false;

        lock (_phoneNumberSync)
        {
            if (!PhoneNumbersEquivalent(_currentPhoneNumber, value))
            {
                _currentPhoneNumber = value;
                changed = true;
            }
            else if (!string.Equals(_currentPhoneNumber, value, StringComparison.Ordinal))
            {
                _currentPhoneNumber = value;
            }
        }

        if (changed)
        {
            _logger.LogInformation(
                "Current Windows SMS phone number changed to {PhoneNumber}",
                value);
            CurrentPhoneNumberChanged?.Invoke(this, value);
        }
    }

    private static bool PhoneNumbersEquivalent(string left, string right)
        => string.Equals(
            NormalizePhoneNumber(left),
            NormalizePhoneNumber(right),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizePhoneNumber(string phoneNumber)
    {
        var digits = new string((phoneNumber ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.StartsWith("00")) digits = digits[2..];
        if (digits.StartsWith("0") && digits.Length >= 10) digits = "44" + digits[1..];
        return digits;
    }

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
            var localPhoneNumber = text.To?.Trim();
            if (string.IsNullOrWhiteSpace(localPhoneNumber))
                localPhoneNumber = GetCurrentPhoneNumber();

            UpdateCurrentPhoneNumber(localPhoneNumber);

            var message = new SmsMessage
            {
                PhoneNumber = text.From ?? string.Empty,
                LocalPhoneNumber = localPhoneNumber ?? string.Empty,
                Body = text.Body ?? string.Empty,
                Timestamp = text.Timestamp,
                Direction = SmsDirection.Incoming,
                Status = SmsStatus.Received,
                IsRead = false
            };

            // Acknowledge promptly so Windows can continue normal delivery.
            details.Accept();

            // Blocked senders are discarded before archive storage and before
            // MessageReceived is raised, so they never reach conversations or notifications.
            if (_blockedNumbers.IsBlocked(message.PhoneNumber))
            {
                _logger.LogInformation("Discarded incoming SMS from blocked number {PhoneNumber}", message.PhoneNumber);
                return;
            }

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
