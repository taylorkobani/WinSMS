namespace WinSMS.Models;

public enum SmsDirection
{
    Incoming,
    Outgoing
}

public enum SmsStatus
{
    Pending,
    Sending,
    Sent,
    Received,
    Failed
}

public enum ModemConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Error
}
