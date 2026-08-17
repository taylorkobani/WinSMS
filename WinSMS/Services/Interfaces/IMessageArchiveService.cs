using WinSMS.Models;

namespace WinSMS.Services.Interfaces;

public interface IMessageArchiveService
{
    Task SaveMessageAsync(SmsMessage message);
    Task<IReadOnlyList<SmsMessage>> LoadMessagesForDateAsync(DateOnly date);
    Task<IReadOnlyList<SmsMessage>> LoadAllMessagesAsync();
    Task DeleteMessageAsync(Guid messageId);
    Task UpdateMessageAsync(SmsMessage message);
}
