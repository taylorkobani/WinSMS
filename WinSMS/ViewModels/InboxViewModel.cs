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
                : Conversations.FirstOrDefault(c => c.PhoneNumber == selectedNumber) ?? Conversations.FirstOrDefault();
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
        _dispatcher.TryEnqueue(async () => await RefreshAsync());
    }

    public string? GetSenderNumber() => SelectedConversation?.PhoneNumber;
}
