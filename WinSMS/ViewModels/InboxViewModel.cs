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
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        StatusMessage = null;
        try
        {
            var selectedNumber = SelectedConversation?.PhoneNumber;
            var conversations = await _archive.LoadConversationsAsync();
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
                await RefreshAsync();
            }
            else
            {
                StatusMessage = sent.Error ?? "Failed to send message.";
                await RefreshAsync();
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
        // SmsService archives the message before raising this event. Reloading from
        // the archive makes the XML conversation the single source of truth and
        // also replaces SelectedConversation so x:Bind re-evaluates its Messages.
        _dispatcher.TryEnqueue(async () =>
        {
            var incomingKey = NormalizePhoneNumber(message.PhoneNumber);
            var selectedKey = SelectedConversation == null
                ? null
                : NormalizePhoneNumber(SelectedConversation.PhoneNumber);

            await RefreshAsync();

            var incomingConversation = Conversations.FirstOrDefault(
                c => NormalizePhoneNumber(c.PhoneNumber) == incomingKey);

            // Keep the user's current conversation selected unless this is the
            // conversation that just received the message.
            if (incomingConversation != null &&
                (selectedKey == null || selectedKey == incomingKey))
            {
                SelectedConversation = null;
                SelectedConversation = incomingConversation;
            }
        });
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
