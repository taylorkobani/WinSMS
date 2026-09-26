using System.Text.Json;

namespace WinSMS.Services;

public sealed class PhoneProfileService
{
    private readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WinSMS", "phone-profiles.json");

    public event EventHandler? ProfilesChanged;

    public PhoneProfile GetProfile(string phoneNumber)
    {
        var key = NormalizePhoneNumber(phoneNumber);
        if (string.IsNullOrWhiteSpace(key))
            return new PhoneProfile();

        var profiles = Load();
        return profiles.TryGetValue(key, out var profile) ? profile : new PhoneProfile();
    }

    public async Task SaveProfileAsync(string phoneNumber, string name, string color)
    {
        var key = NormalizePhoneNumber(phoneNumber);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Windows did not provide a phone number for the current SMS account.");

        var profiles = Load();
        profiles[key] = new PhoneProfile
        {
            Name = name?.Trim() ?? string.Empty,
            Color = string.IsNullOrWhiteSpace(color) ? "#0078D4" : color
        };

        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        await File.WriteAllTextAsync(
            _filePath,
            JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true }));

        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    private Dictionary<string, PhoneProfile> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new Dictionary<string, PhoneProfile>(StringComparer.OrdinalIgnoreCase);

            return JsonSerializer.Deserialize<Dictionary<string, PhoneProfile>>(File.ReadAllText(_filePath))
                   ?? new Dictionary<string, PhoneProfile>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, PhoneProfile>(StringComparer.OrdinalIgnoreCase);
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

public sealed class PhoneProfile
{
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#0078D4";
}
