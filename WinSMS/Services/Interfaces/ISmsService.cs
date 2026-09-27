using WinSMS.Models;

namespace WinSMS.Services.Interfaces;

public interface ISmsService
{
    Task<IReadOnlyList<SmsMessage>> GetAllMessagesAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SmsMessage>> GetUnreadMessagesAsync(
        CancellationToken cancellationToken = default);

    Task<SmsMessage> SendMessageAsync(
        string phoneNumber,
        string body,
        CancellationToken cancellationToken = default);

    Task MarkAsReadAsync(SmsMessage message);

    CellularSubscription? GetCurrentSubscription();

    Task<CellularSubscription?> SynchronizeCurrentSubscriptionAsync(
        CancellationToken cancellationToken = default);

    Task<CellularSubscription?> SwitchCurrentSubscriptionAsync(
        bool useEsim,
        string? targetIccId = null,
        CancellationToken cancellationToken = default);

    event EventHandler<SmsMessage>? MessageReceived;
    event EventHandler<CellularSubscription>? CurrentSubscriptionChanged;
}
