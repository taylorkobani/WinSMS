using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using WinSMS.Models;
using WinSMS.Services.Interfaces;

namespace WinSMS.ViewModels;

public partial class InboxViewModel : ObservableObject
{
    private readonly ISmsService _smsService;
    private readonly IMessageArchiveService _archive;
    private readonly DispatcherQueue _dispatcher;

    // Compatibility aliases retained for WinUI incremental XAML compilation.
    // The Inbox UI itself is conversation-based.
    public ObservableCollection<SmsMessage> Messages { get; } = new();
    public SmsMessage? SelectedMessage { get; set; }

    [RelayCommand]
    private async Task MarkAsReadAsync(SmsMessage? message)
    {
        if (message == null || message.IsRead) return;
        message.IsRead = true;
        await _archive.UpdateMessageAsync(message);
        await RefreshAsync();
    }

    [ObservableProperty]
    private ObservableCollection<SmsConversation> _conversations = new();

    [ObservableProperty]
    private SmsConversation? _selectedConversation;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendReplyCommand))]
    private string _replyBody = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendReplyCommand))]
    private bool _isSending;

    public InboxViewModel(ISmsService smsService, IMessageArchiveService archive)
    {
        _smsService = smsService;
        _archive = archive;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _smsService.MessageReceived += OnMessageReceived;
    }

    public Task LoadAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        StatusMessage = null;
        try
        {
            var selectedNumber = SelectedConversation?.PhoneNumber;
            var localPhoneNumber = _smsService.GetCurrentPhoneNumber();
            if (string.IsNullOrWhiteSpace(localPhoneNumber))
            {
                StatusMessage = "Windows did not provide a phone number for the current SMS account.";
                Conversations.Clear();
                SelectedConversation = null;
                return;
            }

            var conversations = await _archive.LoadConversationsAsync(localPhoneNumber);
            Conversations.Clear();
            foreach (var conversation in conversations)
                Conversations.Add(conversation);
            SelectedConversation = selectedNumber == null
                ? Conversations.FirstOrDefault()
                : Conversations.FirstOrDefault(c =>
                    NormalizePhoneNumber(c.PhoneNumber) == NormalizePhoneNumber(selectedNumber))
                  ?? Conversations.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to refresh conversations: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanSendReply()
        => SelectedConversation != null && !string.IsNullOrWhiteSpace(ReplyBody) && !IsSending;

    partial void OnSelectedConversationChanged(SmsConversation? value)
        => SendReplyCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanSendReply))]
    private async Task SendReplyAsync()
    {
        if (SelectedConversation == null || string.IsNullOrWhiteSpace(ReplyBody)) return;

        var number = SelectedConversation.PhoneNumber;
        var body = ReplyBody.Trim();
        IsSending = true;
        StatusMessage = null;
        try
        {
            var sent = await _smsService.SendMessageAsync(number, body);
            if (sent.Status == SmsStatus.Sent)
            {
                ReplyBody = string.Empty;
                AddMessageToConversation(SelectedConversation, sent);
            }
            else
            {
                StatusMessage = sent.Error ?? "Failed to send message.";
                AddMessageToConversation(SelectedConversation, sent);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to send message: {ex.Message}";
        }
        finally
        {
            IsSending = false;
        }
    }

    [RelayCommand]
    private async Task MarkConversationAsReadAsync(SmsConversation conversation)
    {
        foreach (var message in conversation.Messages.Where(m => m.Direction == SmsDirection.Incoming && !m.IsRead))
        {
            message.IsRead = true;
            await _archive.UpdateMessageAsync(message);
        }
        await RefreshAsync();
    }

    [RelayCommand]
    public async Task DeleteConversationAsync(SmsConversation? conversation)
    {
        if (conversation == null) return;

        try
        {
            var number = conversation.PhoneNumber;
            await _archive.DeleteConversationAsync(conversation.LocalPhoneNumber, number);
            var wasSelected = ReferenceEquals(SelectedConversation, conversation);
            Conversations.Remove(conversation);

            if (wasSelected)
            {
                ReplyBody = string.Empty;
                SelectedConversation = Conversations.FirstOrDefault();
            }

            StatusMessage = $"Conversation with {number} deleted.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to delete conversation: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DeleteMessageAsync(SmsMessage message)
    {
        try
        {
            await _archive.DeleteMessageAsync(message.Id);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to delete message: {ex.Message}";
        }
    }

    private void OnMessageReceived(object? sender, SmsMessage message)
    {
        _dispatcher.TryEnqueue(() =>
        {
            var currentLocalKey = NormalizePhoneNumber(_smsService.GetCurrentPhoneNumber());
            if (string.IsNullOrWhiteSpace(currentLocalKey) ||
                NormalizePhoneNumber(message.LocalPhoneNumber) != currentLocalKey)
                return;

            var key = NormalizePhoneNumber(message.PhoneNumber);
            var conversation = Conversations.FirstOrDefault(
                c => NormalizePhoneNumber(c.PhoneNumber) == key);

            if (conversation == null)
            {
                conversation = new SmsConversation
                {
                    PhoneNumber = message.PhoneNumber,
                    LocalPhoneNumber = message.LocalPhoneNumber
                };
                conversation.Messages.Add(message);
                Conversations.Insert(0, conversation);

                if (SelectedConversation == null)
                    SelectedConversation = conversation;

                return;
            }

            AddMessageToConversation(conversation, message);
        });
    }

    private static void AddMessageToConversation(SmsConversation conversation, SmsMessage message)
    {
        if (conversation.Messages.Any(m => m.Id == message.Id))
            return;

        conversation.Messages.Add(message);
    }

    private static string NormalizePhoneNumber(string phoneNumber)
    {
        var digits = new string((phoneNumber ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.StartsWith("00")) digits = digits[2..];
        if (digits.StartsWith("0") && digits.Length >= 10) digits = "44" + digits[1..];
        return digits;
    }

    public string? GetSenderNumber() => SelectedConversation?.PhoneNumber;
}
