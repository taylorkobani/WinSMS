using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinSMS.Models;
using WinSMS.Services.Interfaces;

namespace WinSMS.ViewModels;

public partial class InboxViewModel : ObservableObject
{
    private readonly ISmsService _smsService;
    private readonly IMessageArchiveService _archive;

    [ObservableProperty]
    private ObservableCollection<SmsMessage> _messages = new();

    [ObservableProperty]
    private SmsMessage? _selectedMessage;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _statusMessage;

    public InboxViewModel(ISmsService smsService, IMessageArchiveService archive)
    {
        _smsService = smsService;
        _archive = archive;
        _smsService.MessageReceived += OnMessageReceived;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        StatusMessage = null;
        try
        {
            var messages = await _smsService.GetAllMessagesAsync();
            Messages.Clear();
            foreach (var m in messages.OrderByDescending(x => x.Timestamp))
                Messages.Add(m);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to refresh inbox: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task MarkAsReadAsync(SmsMessage message)
    {
        if (message.IsRead) return;
        await _smsService.MarkAsReadAsync(message);
        // Replace item in collection to trigger ListView refresh
        var index = Messages.IndexOf(message);
        if (index >= 0)
        {
            Messages.RemoveAt(index);
            Messages.Insert(index, message);
        }
    }

    [RelayCommand]
    private async Task DeleteMessageAsync(SmsMessage message)
    {
        try
        {
            if (message.ModemMessageIndex.HasValue)
                await _smsService.DeleteMessageAsync(message.ModemMessageIndex.Value);
            await _archive.DeleteMessageAsync(message.Id);
            Messages.Remove(message);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to delete message: {ex.Message}";
        }
    }

    private void OnMessageReceived(object? sender, SmsMessage message)
    {
        Messages.Insert(0, message);
    }

    public string? GetSenderNumber() => SelectedMessage?.PhoneNumber;
}
