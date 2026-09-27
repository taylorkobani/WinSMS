using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using WinSMS.Models;
using WinSMS.Services.Interfaces;
using WinSMS.Services;

namespace WinSMS.ViewModels;

public partial class InboxViewModel : ObservableObject
{
    private readonly ISmsService _smsService;
    private readonly IMessageArchiveService _archive;
    private readonly DispatcherQueue _dispatcher;
    private readonly BlockedNumberService _blockedNumbers;
    private readonly PhoneProfileService _phoneProfiles;

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
    private bool _hasConversations;

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

    public InboxViewModel(
        ISmsService smsService,
        IMessageArchiveService archive,
        BlockedNumberService blockedNumbers,
        PhoneProfileService phoneProfiles)
    {
        _smsService = smsService;
        _archive = archive;
        _blockedNumbers = blockedNumbers;
        _phoneProfiles = phoneProfiles;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _smsService.MessageReceived += OnMessageReceived;
        _smsService.CurrentSubscriptionChanged += OnCurrentSubscriptionChanged;
    }

    public Task LoadAsync() => RefreshAsync();

    public async Task SelectConversationAsync(string phoneNumber)
    {
        var key = NormalizePhoneNumber(phoneNumber);
        var conversation = Conversations.FirstOrDefault(
            c => NormalizePhoneNumber(c.PhoneNumber) == key);

        if (conversation == null)
        {
            await RefreshAsync();
            conversation = Conversations.FirstOrDefault(
                c => NormalizePhoneNumber(c.PhoneNumber) == key);
        }

        if (conversation != null)
            SelectedConversation = conversation;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        StatusMessage = null;

        try
        {
            var selectedNumber = SelectedConversation?.PhoneNumber;
            var subscription =
                await _smsService.SynchronizeCurrentSubscriptionAsync();

            if (subscription == null ||
                string.IsNullOrWhiteSpace(subscription.IccId))
            {
                StatusMessage =
                    "Windows did not provide an ICCID for the current cellular subscription.";

                Conversations.Clear();
                HasConversations = false;
                SelectedConversation = null;
                return;
            }

            var profile = _phoneProfiles.GetProfile(subscription.IccId);

            var conversations = await _archive.LoadConversationsAsync(
                subscription.IccId,
                profile.PhoneNumber);

            Conversations.Clear();

            foreach (var conversation in conversations)
                Conversations.Add(conversation);

            HasConversations = Conversations.Count > 0;

            SelectedConversation = selectedNumber == null
                ? Conversations.FirstOrDefault()
                : Conversations.FirstOrDefault(conversation =>
                    NormalizePhoneNumber(conversation.PhoneNumber) ==
                    NormalizePhoneNumber(selectedNumber))
                  ?? Conversations.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusMessage =
                $"Failed to refresh conversations: {ex.Message}";
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

        var conversation = SelectedConversation;
        var number = conversation.PhoneNumber;
        var body = ReplyBody.Trim();
        IsSending = true;
        StatusMessage = null;
        try
        {
            var sent = await _smsService.SendMessageAsync(number, body);
            if (sent.Status == SmsStatus.Sent)
            {
                ReplyBody = string.Empty;
                AddMessageToConversation(conversation, sent);
            }
            else
            {
                StatusMessage = sent.Error ?? "Failed to send message.";
                AddMessageToConversation(conversation, sent);
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

    public bool IsBlocked(SmsConversation conversation)
        => _blockedNumbers.IsBlocked(conversation.PhoneNumber);

    public async Task<bool> ToggleBlockedAsync(SmsConversation? conversation)
    {
        if (conversation == null) return false;
        var blocked = await _blockedNumbers.ToggleAsync(conversation.PhoneNumber);
        StatusMessage = blocked
            ? $"{conversation.PhoneNumber} blocked. Future messages will be discarded."
            : $"{conversation.PhoneNumber} unblocked.";
        return blocked;
    }

    [RelayCommand]
    public async Task DeleteConversationAsync(SmsConversation? conversation)
    {
        if (conversation == null) return;

        try
        {
            var number = conversation.PhoneNumber;
            await _archive.DeleteConversationAsync(
                conversation.LocalSubscriptionId,
                number,
                conversation.LocalPhoneNumber);
            var wasSelected = ReferenceEquals(SelectedConversation, conversation);
            Conversations.Remove(conversation);
            HasConversations = Conversations.Count > 0;

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

    private void OnCurrentSubscriptionChanged(
        object? sender,
        CellularSubscription subscription)
    {
        _dispatcher.TryEnqueue(async () => await RefreshAsync());
    }

    private void OnMessageReceived(object? sender, SmsMessage message)
    {
        _dispatcher.TryEnqueue(() =>
        {
            var currentSubscription = _smsService.GetCurrentSubscription();
            var currentSubscriptionKey =
                NormalizeIccId(currentSubscription?.IccId ?? string.Empty);

            if (string.IsNullOrWhiteSpace(currentSubscriptionKey) ||
                NormalizeIccId(message.LocalSubscriptionId) != currentSubscriptionKey)
            {
                return;
            }

            var key = NormalizePhoneNumber(message.PhoneNumber);
            var conversation = Conversations.FirstOrDefault(
                c => NormalizePhoneNumber(c.PhoneNumber) == key);

            if (conversation == null)
            {
                conversation = new SmsConversation
                {
                    PhoneNumber = message.PhoneNumber,
                    LocalSubscriptionId = message.LocalSubscriptionId,
                    LocalPhoneNumber = message.LocalPhoneNumber
                };
                conversation.Messages.Add(message);
                Conversations.Insert(0, conversation);
                HasConversations = true;

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

    private static string NormalizeIccId(string value)
        => new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

    private static string NormalizePhoneNumber(string phoneNumber)
    {
        var digits = new string((phoneNumber ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.StartsWith("00")) digits = digits[2..];
        if (digits.StartsWith("0") && digits.Length >= 10) digits = "44" + digits[1..];
        return digits;
    }

    public string? GetSenderNumber() => SelectedConversation?.PhoneNumber;
}
