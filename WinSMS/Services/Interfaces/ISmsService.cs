using WinSMS.Models;

namespace WinSMS.Services.Interfaces;

public interface ISmsService
{
    Task<IReadOnlyList<SmsMessage>> GetAllMessagesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SmsMessage>> GetUnreadMessagesAsync(CancellationToken cancellationToken = default);
    Task<SmsMessage> SendMessageAsync(string phoneNumber, string body, CancellationToken cancellationToken = default);
    Task MarkAsReadAsync(SmsMessage message);
    string GetCurrentPhoneNumber();
    Task<string> SynchronizeCurrentPhoneNumberAsync(CancellationToken cancellationToken = default);

    event EventHandler<SmsMessage>? MessageReceived;
    event EventHandler<string>? CurrentPhoneNumberChanged;
}
