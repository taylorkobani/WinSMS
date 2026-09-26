using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WinSMS.Models;

public class SmsConversation : INotifyPropertyChanged
{
    private ObservableCollection<SmsMessage> _messages = new();

    public SmsConversation()
    {
        _messages.CollectionChanged += OnMessagesChanged;
    }

    public string PhoneNumber { get; set; } = string.Empty;
    public string LocalPhoneNumber { get; set; } = string.Empty;

    public ObservableCollection<SmsMessage> Messages
    {
        get => _messages;
        set
        {
            if (ReferenceEquals(_messages, value)) return;

            _messages.CollectionChanged -= OnMessagesChanged;
            _messages = value ?? new ObservableCollection<SmsMessage>();
            _messages.CollectionChanged += OnMessagesChanged;
            NotifyConversationSummaryChanged();
        }
    }

    public SmsMessage? LastMessage => Messages.OrderByDescending(m => m.Timestamp).FirstOrDefault();
    public DateTimeOffset LastMessageTimestamp => LastMessage?.Timestamp ?? DateTimeOffset.MinValue;
    public int UnreadCount => Messages.Count(m => m.Direction == SmsDirection.Incoming && !m.IsRead);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => NotifyConversationSummaryChanged();

    private void NotifyConversationSummaryChanged()
    {
        OnPropertyChanged(nameof(LastMessage));
        OnPropertyChanged(nameof(LastMessageTimestamp));
        OnPropertyChanged(nameof(UnreadCount));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
