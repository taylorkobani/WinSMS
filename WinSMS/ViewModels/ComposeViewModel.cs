using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinSMS.Helpers;
using WinSMS.Models;
using WinSMS.Services.Interfaces;

namespace WinSMS.ViewModels;

public partial class ComposeViewModel : ObservableObject
{
    private readonly ISmsService _smsService;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _phoneNumber = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _messageBody = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isSending;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _sendSucceeded;

    public int CharacterCount => MessageBody.Length;
    public int RemainingCharacters => MessageBody.Length == 0 ? 160 : (160 - (MessageBody.Length % 160)) % 160;
    public int MessageParts => MessageBody.Length == 0 ? 1 : (int)Math.Ceiling(MessageBody.Length / 160.0);

    public ComposeViewModel(ISmsService smsService)
    {
        _smsService = smsService;
    }

    partial void OnMessageBodyChanged(string value)
    {
        OnPropertyChanged(nameof(CharacterCount));
        OnPropertyChanged(nameof(RemainingCharacters));
        OnPropertyChanged(nameof(MessageParts));
    }

    private bool CanSend =>
        !IsSending
        && !string.IsNullOrWhiteSpace(PhoneNumber)
        && !string.IsNullOrWhiteSpace(MessageBody)
        && PhoneNumberHelper.IsValidPhoneNumber(PhoneNumber.Trim());

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        IsSending = true;
        StatusMessage = null;
        SendSucceeded = false;
        try
        {
            var message = await _smsService.SendMessageAsync(PhoneNumber.Trim(), MessageBody);
            if (message.Status == SmsStatus.Sent)
            {
                StatusMessage = "Message sent successfully.";
                SendSucceeded = true;
                PhoneNumber = string.Empty;
                MessageBody = string.Empty;
            }
            else
            {
                StatusMessage = $"Send failed: {message.Error ?? "Unknown error"}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsSending = false;
        }
    }

    public static bool IsValidPhoneNumber(string number) => PhoneNumberHelper.IsValidPhoneNumber(number);
}
