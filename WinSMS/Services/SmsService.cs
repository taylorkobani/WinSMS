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
    private readonly MobileBroadbandIdentityService _mobileBroadbandIdentity;
    private readonly LegacyMbnSubscriberService _legacyMbn;
    private readonly PhoneProfileService _phoneProfiles;

    private SmsMessageRegistration? _messageRegistration;
    private readonly object _subscriptionSync = new();
    private readonly SemaphoreSlim _subscriptionRefreshLock = new(1, 1);
    private CellularSubscription? _currentSubscription;
    private string _metadataIccId = string.Empty;

    public event EventHandler<SmsMessage>? MessageReceived;
    public event EventHandler<CellularSubscription>? CurrentSubscriptionChanged;

    public SmsService(
        IMessageArchiveService archive,
        ILogger<SmsService> logger,
        BlockedNumberService blockedNumbers,
        MobileBroadbandIdentityService mobileBroadbandIdentity,
        LegacyMbnSubscriberService legacyMbn,
        PhoneProfileService phoneProfiles)
    {
        _archive = archive;
        _logger = logger;
        _blockedNumbers = blockedNumbers;
        _mobileBroadbandIdentity = mobileBroadbandIdentity;
        _legacyMbn = legacyMbn;
        _phoneProfiles = phoneProfiles;

        InitializeWindowsSmsReceiving();
    }

    public CellularSubscription? GetCurrentSubscription()
    {
        lock (_subscriptionSync)
            return _currentSubscription;
    }

    public async Task<CellularSubscription?> SynchronizeCurrentSubscriptionAsync(
        CancellationToken cancellationToken = default)
    {
        await _subscriptionRefreshLock.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<LegacyMbnSubscriberInfo> subscribers;
            try
            {
                subscribers = await Task.Run(
                    () => _legacyMbn.GetSubscribers(),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Could not enumerate current MBN subscriber information.");

                subscribers = Array.Empty<LegacyMbnSubscriberInfo>();
            }

            var subscriber = subscribers.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(item.SimIccId));

            if (subscriber == null)
            {
                // Keep SMS transport functional even on hardware where Windows
                // declines to expose subscriber identity. Without an ICCID we
                // do not create or select a WinSMS profile.
                return GetCurrentSubscription();
            }

            var iccId = NormalizeIccId(subscriber.SimIccId);
            var previous = GetCurrentSubscription();
            var storedProfile = _phoneProfiles.GetProfile(iccId);

            var sameAsPrevious = string.Equals(
                NormalizeIccId(previous?.IccId ?? string.Empty),
                iccId,
                StringComparison.OrdinalIgnoreCase);

            var windowsProfileName = sameAsPrevious
                ? previous?.WindowsProfileName ?? string.Empty
                : storedProfile.WindowsProfileName;

            bool? isEsim = sameAsPrevious
                ? previous?.IsEsim
                : storedProfile.IsEsim;

            var needMetadata = !string.Equals(
                NormalizeIccId(_metadataIccId),
                iccId,
                StringComparison.OrdinalIgnoreCase);

            if (needMetadata)
            {
                try
                {
                    var readyInfo =
                        await _mobileBroadbandIdentity.GetReadyInfoAsync(
                            cancellationToken);

                    windowsProfileName = readyInfo.WindowsProfileName;

                    var matchedSlot = readyInfo.Slots.FirstOrDefault(slot =>
                        !string.IsNullOrWhiteSpace(slot.IccId) &&
                        NormalizeIccId(slot.IccId) == iccId);

                    if (matchedSlot != null)
                    {
                        isEsim = matchedSlot.IsEsim;
                    }
                    else if (readyInfo.SelectedSlot != null &&
                             !string.IsNullOrWhiteSpace(
                                 readyInfo.SelectedSlot.IccId) &&
                             NormalizeIccId(
                                 readyInfo.SelectedSlot.IccId) == iccId)
                    {
                        isEsim = readyInfo.SelectedSlot.IsEsim;
                    }
                    else
                    {
                        var subscriberPhone =
                            subscriber.TelephoneNumbers.FirstOrDefault(number =>
                                !string.IsNullOrWhiteSpace(number));

                        if (readyInfo.SelectedSlot != null &&
                            !string.IsNullOrWhiteSpace(subscriberPhone) &&
                            readyInfo.SelectedSlot.TelephoneNumbers.Any(number =>
                                PhoneNumbersEquivalent(
                                    number,
                                    subscriberPhone)))
                        {
                            // Some drivers omit slot ICCID but do expose the
                            // same telephone number for the selected slot.
                            isEsim = readyInfo.SelectedSlot.IsEsim;
                        }
                        else
                        {
                            // Best-effort fallback for drivers that expose one
                            // dedicated eSIM slot but omit its active ICCID.
                            var eSimSlots = readyInfo.Slots
                                .Where(slot => slot.IsEsim)
                                .ToList();

                            var physicalSlots = readyInfo.Slots
                                .Where(slot => !slot.IsEsim)
                                .ToList();

                            if (eSimSlots.Count == 1 &&
                                physicalSlots.Any() &&
                                !string.IsNullOrWhiteSpace(previous?.IccId) &&
                                NormalizeIccId(previous.IccId) != iccId &&
                                previous.IsEsim == false)
                            {
                                isEsim = true;
                            }
                            else if (eSimSlots.Count == 1 &&
                                     physicalSlots.Any() &&
                                     string.IsNullOrWhiteSpace(subscriberPhone))
                            {
                                // Cold-start fallback for the common Windows
                                // dual-SIM behaviour seen on this class of modem:
                                // SmsDevice2 can keep exposing the physical
                                // SIM's phone number while MBN reports a
                                // different active ICCID with no MSISDN.
                                var smsApiNumber =
                                    SmsDevice2.GetDefault()?
                                        .AccountPhoneNumber?
                                        .Trim() ?? string.Empty;

                                var stalePhysicalProfile =
                                    _phoneProfiles.GetProfiles()
                                        .FirstOrDefault(profile =>
                                            NormalizeIccId(profile.IccId) != iccId &&
                                            !string.IsNullOrWhiteSpace(
                                                profile.PhoneNumber) &&
                                            PhoneNumbersEquivalent(
                                                profile.PhoneNumber,
                                                smsApiNumber));

                                if (stalePhysicalProfile != null)
                                    isEsim = true;
                            }
                        }
                    }

                    _metadataIccId = iccId;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(
                        ex,
                        "Could not read SIM/eSIM metadata for ICCID {IccId}.",
                        iccId);
                }
            }

            var subscription = new CellularSubscription
            {
                IccId = iccId,
                SubscriberId =
                    subscriber.SubscriberId?.Trim() ?? string.Empty,

                // The phone number must come from the same MBN subscriber
                // record as this ICCID. SmsDevice2.AccountPhoneNumber can
                // remain bound to another SIM after a Windows SIM/eSIM switch.
                WindowsPhoneNumber =
                    subscriber.TelephoneNumbers
                        .FirstOrDefault(number =>
                            !string.IsNullOrWhiteSpace(number))?
                        .Trim()
                    ?? string.Empty,

                WindowsProfileName = windowsProfileName,
                InterfaceId =
                    subscriber.InterfaceId?.Trim() ?? string.Empty,
                IsEsim = isEsim
            };

            UpdateCurrentSubscription(subscription);
            await _phoneProfiles.SynchronizeProfileAsync(subscription);

            return subscription;
        }
        finally
        {
            _subscriptionRefreshLock.Release();
        }
    }

    public async Task<CellularSubscription?> SwitchCurrentSubscriptionAsync(
        bool useEsim,
        string? targetIccId = null,
        CancellationToken cancellationToken = default)
    {
        var targetKey = NormalizeIccId(targetIccId ?? string.Empty);
        var current = GetCurrentSubscription();
        var previousKey = NormalizeIccId(current?.IccId ?? string.Empty);

        if (current?.IsEsim == useEsim &&
            (string.IsNullOrWhiteSpace(targetKey) || previousKey == targetKey))
        {
            return current;
        }

        _logger.LogInformation(
            "Requesting cellular slot switch. TargetICCID={IccId}; Type={Type}",
            string.IsNullOrWhiteSpace(targetKey) ? "(unknown)" : targetKey,
            useEsim ? "eSIM" : "SIM");

        var switchResult = await _mobileBroadbandIdentity.SwitchSlotAsync(
            useEsim,
            cancellationToken);

        if (!switchResult.Success)
        {
            throw new InvalidOperationException(
                switchResult.Error ?? "Windows rejected the SIM/eSIM switch.");
        }

        // The slot mapping itself has already been accepted/verified by
        // MobileBroadbandIdentityService. Windows can take a little longer to
        // refresh subscriber metadata (especially ICCID) after the mapping
        // changes, so wait for fresh identity data but do not turn a successful
        // Windows slot change into a false UI error if metadata lags behind.
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(20);
        CellularSubscription? latestSubscription = null;

        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                _metadataIccId = string.Empty;

                var subscription =
                    await SynchronizeCurrentSubscriptionAsync(cancellationToken);

                if (subscription != null)
                {
                    latestSubscription = subscription;
                    var activeKey = NormalizeIccId(subscription.IccId);

                    var iccidConfirmed =
                        !string.IsNullOrWhiteSpace(targetKey) &&
                        activeKey == targetKey;

                    var typeConfirmed =
                        subscription.IsEsim == useEsim &&
                        (!string.IsNullOrWhiteSpace(activeKey) &&
                         activeKey != previousKey);

                    if (iccidConfirmed || typeConfirmed)
                    {
                        _logger.LogInformation(
                            "Cellular slot switch confirmed. ICCID={IccId}; Type={Type}",
                            activeKey,
                            useEsim ? "eSIM" : "SIM");

                        return subscription;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(
                    ex,
                    "Waiting for Windows to refresh the switched cellular subscription.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
        }

        // The requested slot mapping is already active at this point. Some WWAN
        // drivers keep returning stale subscriber metadata for a while. Return
        // the latest known subscription and let the normal background
        // synchronization refresh ICCID/profile metadata when Windows catches up.
        _logger.LogWarning(
            "Cellular slot mapping succeeded, but subscriber metadata did not refresh within 20 seconds.");

        return latestSubscription ?? GetCurrentSubscription();
    }

    public async Task<IReadOnlyList<SmsMessage>> GetAllMessagesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var archived = await _archive.LoadAllMessagesAsync();

        return archived
            .Where(message => message.Direction == SmsDirection.Incoming)
            .OrderBy(message => message.Timestamp)
            .ToList();
    }

    public async Task<IReadOnlyList<SmsMessage>> GetUnreadMessagesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var archived = await _archive.LoadAllMessagesAsync();

        return archived
            .Where(message =>
                message.Direction == SmsDirection.Incoming &&
                !message.IsRead)
            .OrderBy(message => message.Timestamp)
            .ToList();
    }

    public async Task<SmsMessage> SendMessageAsync(
        string phoneNumber,
        string body,
        CancellationToken cancellationToken = default)
    {
        // Keep the proven SMS transport path unchanged: Windows decides which
        // currently selected SIM/eSIM actually sends the message.
        var device = SmsDevice2.GetDefault()
            ?? throw new InvalidOperationException(
                "Windows did not provide a default SMS device.");

        var subscription =
            await SynchronizeCurrentSubscriptionAsync(cancellationToken);

        var localSubscriptionId = subscription?.IccId ?? string.Empty;
        var localPhoneNumber = string.Empty;

        if (!string.IsNullOrWhiteSpace(localSubscriptionId))
        {
            var profile = _phoneProfiles.GetProfile(localSubscriptionId);
            localPhoneNumber = profile.PhoneNumber?.Trim() ?? string.Empty;
        }
        else
        {
            // Compatibility fallback only when Windows did not expose an ICCID.
            localPhoneNumber = device.AccountPhoneNumber?.Trim() ?? string.Empty;
        }

        var message = new SmsMessage
        {
            PhoneNumber = phoneNumber,
            LocalSubscriptionId = localSubscriptionId,
            LocalPhoneNumber = localPhoneNumber,
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
            {
                throw new InvalidOperationException(
                    $"Windows SMS device is not ready. Status: {device.DeviceStatus}.");
            }

            var sms = new SmsTextMessage2
            {
                To = phoneNumber,
                Body = body
            };

            var result =
                await device.SendMessageAndGetResultAsync(sms)
                    .AsTask(cancellationToken);

            if (result.IsSuccessful)
            {
                message.Status = SmsStatus.Sent;
                message.ModemReference =
                    result.MessageReferenceNumbers.Count > 0
                        ? string.Join(",", result.MessageReferenceNumbers)
                        : null;

                _logger.LogInformation(
                    "SMS sent successfully through Windows SMS API to {PhoneNumber}",
                    phoneNumber);
            }
            else
            {
                message.Status = SmsStatus.Failed;
                message.Error =
                    $"Windows SMS send failed. CellularClass={result.CellularClass}; " +
                    $"ModemError={result.ModemErrorCode}; " +
                    $"TransportFailure={result.TransportFailureCause}.";

                _logger.LogError("SMS send failed: {Error}", message.Error);
            }
        }
        catch (Exception ex)
        {
            message.Status = SmsStatus.Failed;
            message.Error = ex.Message;

            _logger.LogError(
                ex,
                "Failed to send SMS to {PhoneNumber}",
                phoneNumber);
        }
        finally
        {
            await _archive.UpdateMessageAsync(message);
        }

        return message;
    }

    public Task MarkAsReadAsync(SmsMessage message)
    {
        message.IsRead = true;
        return _archive.UpdateMessageAsync(message);
    }

    private void UpdateCurrentSubscription(CellularSubscription subscription)
    {
        CellularSubscription? previous;
        var changed = false;

        lock (_subscriptionSync)
        {
            previous = _currentSubscription;

            changed =
                previous == null ||
                !string.Equals(
                    NormalizeIccId(previous.IccId),
                    NormalizeIccId(subscription.IccId),
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    previous.WindowsPhoneNumber,
                    subscription.WindowsPhoneNumber,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    previous.WindowsProfileName,
                    subscription.WindowsProfileName,
                    StringComparison.Ordinal) ||
                previous.IsEsim != subscription.IsEsim;

            _currentSubscription = subscription;
        }

        if (!changed)
            return;

        _logger.LogInformation(
            "Current cellular subscription changed. ICCID={IccId}; Type={Type}; WindowsPhoneNumber={PhoneNumber}",
            subscription.IccId,
            subscription.SimTypeLabel,
            subscription.WindowsPhoneNumber);

        CurrentSubscriptionChanged?.Invoke(this, subscription);
    }

    private void InitializeWindowsSmsReceiving()
    {
        const string registrationId = "WinSMS.TextMessages";

        try
        {
            var existing = SmsMessageRegistration.AllRegistrations
                .FirstOrDefault(registration => registration.Id == registrationId);

            if (existing != null)
            {
                _logger.LogInformation(
                    "Removing stale SMS registration {RegistrationId} before re-registering.",
                    registrationId);

                existing.Unregister();
            }

            var rules = new SmsFilterRules(SmsFilterActionType.Accept);
            rules.Rules.Add(new SmsFilterRule(SmsMessageType.Text));

            _messageRegistration =
                SmsMessageRegistration.Register(registrationId, rules);

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

            var subscription =
                await SynchronizeCurrentSubscriptionAsync();

            var localPhoneNumber = text.To?.Trim() ?? string.Empty;

            // If an incoming SMS gives Windows a concrete local destination
            // number for an ICCID that previously had none, treat that as
            // authoritative Windows metadata and lock the profile number.
            if (subscription != null &&
                !string.IsNullOrWhiteSpace(localPhoneNumber) &&
                !PhoneNumbersEquivalent(
                    subscription.WindowsPhoneNumber,
                    localPhoneNumber))
            {
                subscription = new CellularSubscription
                {
                    IccId = subscription.IccId,
                    SubscriberId = subscription.SubscriberId,
                    WindowsPhoneNumber = localPhoneNumber,
                    WindowsProfileName = subscription.WindowsProfileName,
                    InterfaceId = subscription.InterfaceId,
                    IsEsim = subscription.IsEsim
                };

                UpdateCurrentSubscription(subscription);
                await _phoneProfiles.SynchronizeProfileAsync(subscription);
            }

            if (string.IsNullOrWhiteSpace(localPhoneNumber) &&
                subscription != null)
            {
                localPhoneNumber =
                    _phoneProfiles.GetEffectivePhoneNumber(subscription.IccId);
            }

            var message = new SmsMessage
            {
                PhoneNumber = text.From ?? string.Empty,
                LocalSubscriptionId = subscription?.IccId ?? string.Empty,
                LocalPhoneNumber = localPhoneNumber,
                Body = text.Body ?? string.Empty,
                Timestamp = text.Timestamp,
                Direction = SmsDirection.Incoming,
                Status = SmsStatus.Received,
                IsRead = false
            };

            // Acknowledge promptly so Windows can continue normal delivery.
            details.Accept();

            if (_blockedNumbers.IsBlocked(message.PhoneNumber))
            {
                _logger.LogInformation(
                    "Discarded incoming SMS from blocked number {PhoneNumber}",
                    message.PhoneNumber);

                return;
            }

            await _archive.SaveMessageAsync(message);

            _logger.LogInformation(
                "Incoming SMS received from {PhoneNumber}",
                message.PhoneNumber);

            MessageReceived?.Invoke(this, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to process an incoming Windows SMS message.");

            try { details.Accept(); }
            catch { }
        }
    }

    private static bool PhoneNumbersEquivalent(string left, string right)
        => string.Equals(
            NormalizePhoneNumber(left),
            NormalizePhoneNumber(right),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizePhoneNumber(string value)
    {
        var digits = new string((value ?? string.Empty)
            .Where(char.IsDigit)
            .ToArray());

        if (digits.StartsWith("00"))
            digits = digits[2..];

        if (digits.StartsWith("0") && digits.Length >= 10)
            digits = "44" + digits[1..];

        return digits;
    }

    private static string NormalizeIccId(string value)
        => new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
}
