namespace WinSMS.Models;

public class SmsConversation
{
    public string PhoneNumber { get; set; } = string.Empty;
    public List<SmsMessage> Messages { get; set; } = new();
    public SmsMessage? LastMessage => Messages.OrderByDescending(m => m.Timestamp).FirstOrDefault();
    public DateTimeOffset LastMessageTimestamp => LastMessage?.Timestamp ?? DateTimeOffset.MinValue;
    public int UnreadCount => Messages.Count(m => m.Direction == SmsDirection.Incoming && !m.IsRead);
}
