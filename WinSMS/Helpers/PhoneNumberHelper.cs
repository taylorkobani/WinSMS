using System.Text.RegularExpressions;

namespace WinSMS.Helpers;

public static class PhoneNumberHelper
{
    private static readonly Regex ValidPhoneRegex = new(@"^\+?\d{7,15}$", RegexOptions.Compiled);

    public static bool IsValidPhoneNumber(string? number)
        => ValidPhoneRegex.IsMatch(number?.Trim() ?? string.Empty);
}
