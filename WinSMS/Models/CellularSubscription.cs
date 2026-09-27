namespace WinSMS.Models;

/// <summary>
/// Identity of the cellular subscription currently exposed by Windows.
/// ICCID is the stable WinSMS identity. Phone number is optional metadata.
/// </summary>
public sealed class CellularSubscription
{
    public string IccId { get; init; } = string.Empty;
    public string SubscriberId { get; init; } = string.Empty;
    public string WindowsPhoneNumber { get; init; } = string.Empty;
    public string WindowsProfileName { get; init; } = string.Empty;
    public string InterfaceId { get; init; } = string.Empty;
    public bool? IsEsim { get; init; }

    public bool HasWindowsPhoneNumber =>
        !string.IsNullOrWhiteSpace(WindowsPhoneNumber);

    public string SimTypeLabel => IsEsim switch
    {
        true => "eSIM",
        false => "SIM",
        _ => "SIM / eSIM"
    };
}
