using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
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

    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }
    private string? _statusMessage;

    public ProfilesViewModel(ISmsService smsService, PhoneProfileService phoneProfiles)
    {
        _smsService = smsService;
        _phoneProfiles = phoneProfiles;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _smsService.CurrentPhoneNumberChanged += OnCurrentPhoneNumberChanged;
        _phoneProfiles.ProfilesChanged += OnProfilesChanged;

        Load();
    }

    private void OnCurrentPhoneNumberChanged(object? sender, string phoneNumber)
        => _dispatcher.TryEnqueue(Load);

    private void OnProfilesChanged(object? sender, EventArgs e)
        => _dispatcher.TryEnqueue(Load);

    public void Load()
    {
        var current = NormalizePhoneNumber(_smsService.GetCurrentPhoneNumber());
        Profiles.Clear();

        foreach (var profile in _phoneProfiles.GetProfiles())
            Profiles.Add(new PhoneProfileItem
            {
                PhoneNumber = profile.PhoneNumber,
                Name = profile.Name,
                Color = profile.Color,
                IsCurrent = NormalizePhoneNumber(profile.PhoneNumber) == current
            });

        if (!string.IsNullOrWhiteSpace(current) &&
            !Profiles.Any(p => NormalizePhoneNumber(p.PhoneNumber) == current))
        {
            var profile = _phoneProfiles.GetProfile(current);
            Profiles.Insert(0, new PhoneProfileItem
            {
                PhoneNumber = current,
                Name = profile.Name,
                Color = profile.Color,
                IsCurrent = true
            });
        }
    }

    [RelayCommand]
    private async Task SaveProfileAsync(PhoneProfileItem? profile)
    {
        if (profile is null) return;

        try
        {
            await _phoneProfiles.SaveProfileAsync(profile.PhoneNumber, profile.Name, profile.Color);
            StatusMessage = $"Profile for {profile.PhoneNumber} saved.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to save profile: {ex.Message}";
        }
    }

    private static string NormalizePhoneNumber(string phoneNumber)
    {
        var digits = new string((phoneNumber ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.StartsWith("00")) digits = digits[2..];
        if (digits.StartsWith("0") && digits.Length >= 10) digits = "44" + digits[1..];
        return digits;
    }
}

public partial class PhoneProfileItem : ObservableObject
{
    public string PhoneNumber { get; set; } = string.Empty;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }
    private string _name = string.Empty;

    public string Color
    {
        get => _color;
        set => SetProperty(ref _color, value);
    }
    private string _color = "#0078D4";

    public bool IsCurrent { get; set; }
}
