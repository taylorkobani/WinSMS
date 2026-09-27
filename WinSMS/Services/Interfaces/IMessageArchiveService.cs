using WinSMS.Models;

namespace WinSMS.Services.Interfaces;

public interface IMessageArchiveService
{
    Task SaveMessageAsync(SmsMessage message);
    Task<IReadOnlyList<SmsMessage>> LoadMessagesForDateAsync(DateOnly date);
    Task<IReadOnlyList<SmsMessage>> LoadAllMessagesAsync();
    Task DeleteMessageAsync(Guid messageId);
    Task UpdateMessageAsync(SmsMessage message);

    Task<IReadOnlyList<SmsConversation>> LoadConversationsAsync(
        string localSubscriptionId,
        string? legacyLocalPhoneNumber = null);

    Task<SmsConversation?> LoadConversationAsync(
        string localSubscriptionId,
        string phoneNumber,
        string? legacyLocalPhoneNumber = null);

    Task DeleteConversationAsync(
        string localSubscriptionId,
        string phoneNumber,
        string? legacyLocalPhoneNumber = null);
}
