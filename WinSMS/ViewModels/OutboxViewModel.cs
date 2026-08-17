using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinSMS.Models;
using WinSMS.Services.Interfaces;

namespace WinSMS.ViewModels;

public partial class OutboxViewModel : ObservableObject
{
    private readonly IMessageArchiveService _archive;

    [ObservableProperty]
    private ObservableCollection<SmsMessage> _messages = new();

    [ObservableProperty]
    private SmsMessage? _selectedMessage;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _statusMessage;

    public OutboxViewModel(IMessageArchiveService archive)
    {
        _archive = archive;
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        StatusMessage = null;
        try
        {
            var all = await _archive.LoadAllMessagesAsync();
            var outgoing = all.Where(m => m.Direction == SmsDirection.Outgoing)
                              .OrderByDescending(m => m.Timestamp);
            Messages.Clear();
            foreach (var m in outgoing)
                Messages.Add(m);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load outbox: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void AddMessage(SmsMessage message)
    {
        Messages.Insert(0, message);
    }

    public void UpdateMessage(SmsMessage message)
    {
        var existing = Messages.FirstOrDefault(m => m.Id == message.Id);
        if (existing != null)
        {
            var index = Messages.IndexOf(existing);
            Messages[index] = message;
        }
    }
}
