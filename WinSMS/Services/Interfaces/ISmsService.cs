using WinSMS.Models;

namespace WinSMS.Services.Interfaces;

public interface ISmsService
{
    Task<IReadOnlyList<SmsMessage>> GetAllMessagesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SmsMessage>> GetUnreadMessagesAsync(CancellationToken cancellationToken = default);
    Task<SmsMessage?> GetMessageByIndexAsync(int index, CancellationToken cancellationToken = default);
    Task<SmsMessage> SendMessageAsync(string phoneNumber, string body, CancellationToken cancellationToken = default);
    Task DeleteMessageAsync(int modemIndex, CancellationToken cancellationToken = default);
    Task MarkAsReadAsync(SmsMessage message);

    event EventHandler<SmsMessage>? MessageReceived;
}
