namespace WinSMS.Models;

public class SmsMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PhoneNumber { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public SmsDirection Direction { get; set; }
    public SmsStatus Status { get; set; }
    public bool IsRead { get; set; }
    public int? ModemMessageIndex { get; set; }
    public string? ModemReference { get; set; }
    public string? Error { get; set; }
}
