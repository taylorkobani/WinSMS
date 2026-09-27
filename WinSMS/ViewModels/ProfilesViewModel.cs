using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using WinSMS.Helpers;
using WinSMS.Models;
using WinSMS.Services;
using WinSMS.Services.Interfaces;

namespace WinSMS.ViewModels;

public partial class ProfilesViewModel : ObservableObject
{
    private readonly ISmsService _smsService;
    private readonly PhoneProfileService _phoneProfiles;
    private readonly DispatcherQueue _dispatcher;

    public IReadOnlyList<string> ProfileColors { get; } = new[]
    {
        "#0078D4", "#107C10", "#E81123", "#FF8C00",
        "#744DA9", "#008272", "#E3008C", "#5D5A58"
    };

    public ObservableCollection<PhoneProfileItem> Profiles { get; } = new();

    [ObservableProperty]
    private string? _statusMessage;

    public ProfilesViewModel(
        ISmsService smsService,
        PhoneProfileService phoneProfiles)
    {
        _smsService = smsService;
        _phoneProfiles = phoneProfiles;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _smsService.CurrentSubscriptionChanged += OnCurrentSubscriptionChanged;
        _phoneProfiles.ProfilesChanged += OnProfilesChanged;

        Load();
    }

    private void OnCurrentSubscriptionChanged(
        object? sender,
        CellularSubscription subscription)
        => _dispatcher.TryEnqueue(Load);

    private void OnProfilesChanged(object? sender, EventArgs e)
        => _dispatcher.TryEnqueue(Load);

    public void Load()
    {
        var currentIccId =
            NormalizeIccId(_smsService.GetCurrentSubscription()?.IccId ?? string.Empty);

        Profiles.Clear();

        foreach (var profile in _phoneProfiles.GetProfiles())
        {
            Profiles.Add(new PhoneProfileItem
            {
                IccId = profile.IccId,
                SubscriberId = profile.SubscriberId,
                PhoneNumber = profile.PhoneNumber,
                PhoneNumberIsReadOnly = profile.IsPhoneNumberFromWindows,
                Name = profile.Name,
                Color = profile.Color,
                IsEsim = profile.IsEsim,
                WindowsProfileName = profile.WindowsProfileName,
                IsCurrent =
                    NormalizeIccId(profile.IccId) == currentIccId
            });
        }
    }

    [RelayCommand]
    private async Task SaveProfileAsync(PhoneProfileItem? profile)
    {
        if (profile is null)
            return;

        if (!profile.PhoneNumberIsReadOnly &&
            !string.IsNullOrWhiteSpace(profile.PhoneNumber) &&
            !PhoneNumberHelper.IsValidPhoneNumber(profile.PhoneNumber))
        {
            StatusMessage =
                "Enter a valid phone number or leave the phone number blank.";
            return;
        }

        try
        {
            await _phoneProfiles.SaveProfileAsync(
                profile.IccId,
                profile.Name,
                profile.Color,
                profile.PhoneNumber);

            StatusMessage =
                $"{profile.SimTypeLabel} profile saved.";
        }
        catch (Exception ex)
        {
            StatusMessage =
                $"Failed to save profile: {ex.Message}";
        }
    }

    private static string NormalizeIccId(string value)
        => new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
}

public partial class PhoneProfileItem : ObservableObject
{
    public string IccId { get; set; } = string.Empty;
    public string SubscriberId { get; set; } = string.Empty;

    [ObservableProperty]
    private string _phoneNumber = string.Empty;

    public bool PhoneNumberIsReadOnly { get; set; }

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _color = "#0078D4";

    public bool? IsEsim { get; set; }
    public string WindowsProfileName { get; set; } = string.Empty;
    public bool IsCurrent { get; set; }

    public string SimTypeLabel => IsEsim switch
    {
        true => "eSIM",
        false => "SIM",
        _ => "SIM / eSIM"
    };

    public string PhoneNumberSourceText => PhoneNumberIsReadOnly
        ? "Reported by Windows — read only"
        : "Not reported by Windows — you can enter it manually";

    public string WindowsProfileDisplay =>
        string.IsNullOrWhiteSpace(WindowsProfileName)
            ? "(not reported)"
            : WindowsProfileName;

    public string SubscriberIdDisplay =>
        string.IsNullOrWhiteSpace(SubscriberId)
            ? "(not reported)"
            : SubscriberId;
}
