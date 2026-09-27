using System.Text.Json;
using WinSMS.Models;

namespace WinSMS.Services;

/// <summary>
/// Stores user-facing profile metadata for cellular subscriptions.
/// ICCID is the persistent identity key. Phone number is metadata and may be
/// unavailable for an otherwise fully usable SIM/eSIM.
/// </summary>
public sealed class PhoneProfileService
{
    private readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WinSMS", "phone-profiles.json");

    public event EventHandler? ProfilesChanged;

    public PhoneProfile GetProfile(string iccId)
    {
        var key = NormalizeIccId(iccId);
        if (string.IsNullOrWhiteSpace(key))
            return new PhoneProfile();

        var profiles = Load();
        if (profiles.TryGetValue(key, out var profile))
            return profile;

        return profiles.Values.FirstOrDefault(profile =>
                   NormalizeIccId(profile.IccId) == key)
               ?? new PhoneProfile();
    }

    public IReadOnlyList<PhoneProfile> GetProfiles()
        => Load()
            .Values
            .Where(profile => !string.IsNullOrWhiteSpace(profile.IccId))
            .GroupBy(profile => NormalizeIccId(profile.IccId), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(profile => profile.Name)
            .ThenBy(profile => profile.IccId)
            .ToList();

    public string GetEffectivePhoneNumber(string iccId)
        => GetProfile(iccId).PhoneNumber?.Trim() ?? string.Empty;

    public async Task<PhoneProfile?> SynchronizeProfileAsync(
        CellularSubscription subscription)
    {
        var key = NormalizeIccId(subscription.IccId);
        if (string.IsNullOrWhiteSpace(key))
            return null;

        var profiles = Load();
        var changed = false;

        PhoneProfile? profile = null;
        string? existingKey = null;

        if (profiles.TryGetValue(key, out var exact))
        {
            profile = exact;
            existingKey = key;
        }
        else
        {
            var pair = profiles.FirstOrDefault(entry =>
                NormalizeIccId(entry.Value.IccId) == key);

            if (!string.IsNullOrWhiteSpace(pair.Key))
            {
                profile = pair.Value;
                existingKey = pair.Key;
            }
        }

        // Migrate the old phone-number-keyed profile format when the currently
        // inserted SIM exposes the same Windows phone number.
        if (profile == null && !string.IsNullOrWhiteSpace(subscription.WindowsPhoneNumber))
        {
            var windowsNumber = NormalizePhoneNumber(subscription.WindowsPhoneNumber);
            var legacyPair = profiles.FirstOrDefault(entry =>
                string.IsNullOrWhiteSpace(entry.Value.IccId) &&
                (NormalizePhoneNumber(entry.Value.PhoneNumber) == windowsNumber ||
                 NormalizePhoneNumber(entry.Key) == windowsNumber));

            if (!string.IsNullOrWhiteSpace(legacyPair.Key))
            {
                profile = legacyPair.Value;
                existingKey = legacyPair.Key;
                changed = true;
            }
        }

        profile ??= new PhoneProfile
        {
            IccId = key,
            Color = "#0078D4"
        };

        changed |= SetIfDifferent(profile.IccId, key, value => profile.IccId = value);
        changed |= SetIfDifferent(
            profile.SubscriberId,
            subscription.SubscriberId?.Trim() ?? string.Empty,
            value => profile.SubscriberId = value);

        if (subscription.IsEsim.HasValue &&
            profile.IsEsim != subscription.IsEsim)
        {
            profile.IsEsim = subscription.IsEsim;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(subscription.WindowsProfileName) &&
            !string.Equals(
                profile.WindowsProfileName,
                subscription.WindowsProfileName.Trim(),
                StringComparison.Ordinal))
        {
            profile.WindowsProfileName = subscription.WindowsProfileName.Trim();
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(subscription.WindowsPhoneNumber))
        {
            var windowsPhone = subscription.WindowsPhoneNumber.Trim();
            if (!string.Equals(
                    profile.PhoneNumber,
                    windowsPhone,
                    StringComparison.Ordinal))
            {
                profile.PhoneNumber = windowsPhone;
                changed = true;
            }

            if (!profile.IsPhoneNumberFromWindows)
            {
                profile.IsPhoneNumberFromWindows = true;
                changed = true;
            }
        }
        else if (profile.IsPhoneNumberFromWindows)
        {
            // If Windows no longer reports a number for this ICCID, retain the
            // last value but allow the user to correct/replace it.
            profile.IsPhoneNumberFromWindows = false;
            changed = true;
        }

        if (!string.Equals(existingKey, key, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(existingKey))
                profiles.Remove(existingKey);

            profiles[key] = profile;
            changed = true;
        }
        else if (!profiles.ContainsKey(key))
        {
            profiles[key] = profile;
            changed = true;
        }

        if (changed)
            await SaveAsync(profiles);

        return profile;
    }

    public async Task SaveProfileAsync(
        string iccId,
        string name,
        string color,
        string phoneNumber)
    {
        var key = NormalizeIccId(iccId);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                "Windows did not provide an ICCID for this cellular subscription.");

        var profiles = Load();
        if (!profiles.TryGetValue(key, out var profile))
        {
            profile = new PhoneProfile
            {
                IccId = key,
                Color = "#0078D4"
            };
            profiles[key] = profile;
        }

        profile.Name = name?.Trim() ?? string.Empty;
        profile.Color = string.IsNullOrWhiteSpace(color)
            ? "#0078D4"
            : color;

        if (!profile.IsPhoneNumberFromWindows)
            profile.PhoneNumber = phoneNumber?.Trim() ?? string.Empty;

        await SaveAsync(profiles);
    }

    private async Task SaveAsync(Dictionary<string, PhoneProfile> profiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        await File.WriteAllTextAsync(
            _filePath,
            JsonSerializer.Serialize(
                profiles,
                new JsonSerializerOptions { WriteIndented = true }));

        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    private Dictionary<string, PhoneProfile> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return NewDictionary();

            var loaded = JsonSerializer.Deserialize<Dictionary<string, PhoneProfile>>(
                File.ReadAllText(_filePath));

            if (loaded == null)
                return NewDictionary();

            var result = NewDictionary();
            foreach (var pair in loaded)
                result[pair.Key] = pair.Value ?? new PhoneProfile();

            return result;
        }
        catch
        {
            return NewDictionary();
        }
    }

    private static Dictionary<string, PhoneProfile> NewDictionary()
        => new(StringComparer.OrdinalIgnoreCase);

    private static bool SetIfDifferent(
        string current,
        string next,
        Action<string> setter)
    {
        if (string.Equals(current, next, StringComparison.Ordinal))
            return false;

        setter(next);
        return true;
    }

    private static string NormalizeIccId(string value)
        => new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

    private static string NormalizePhoneNumber(string phoneNumber)
    {
        var digits = new string((phoneNumber ?? string.Empty)
            .Where(char.IsDigit)
            .ToArray());

        if (digits.StartsWith("00"))
            digits = digits[2..];

        if (digits.StartsWith("0") && digits.Length >= 10)
            digits = "44" + digits[1..];

        return digits;
    }
}

public sealed class PhoneProfile
{
    /// <summary>Stable WinSMS profile identity.</summary>
    public string IccId { get; set; } = string.Empty;

    /// <summary>IMSI/subscriber ID, retained as secondary diagnostic metadata.</summary>
    public string SubscriberId { get; set; } = string.Empty;

    /// <summary>
    /// Optional telephone number. Windows owns this value when
    /// IsPhoneNumberFromWindows is true; otherwise the user may edit it.
    /// </summary>
    public string PhoneNumber { get; set; } = string.Empty;

    public bool IsPhoneNumberFromWindows { get; set; }

    /// <summary>User-defined WinSMS profile name.</summary>
    public string Name { get; set; } = string.Empty;

    public string Color { get; set; } = "#0078D4";

    /// <summary>Best-effort SIM/eSIM classification from Windows slot metadata.</summary>
    public bool? IsEsim { get; set; }

    /// <summary>
    /// Windows mobile-broadband connection profile name. Display metadata only;
    /// never used as an identity key.
    /// </summary>
    public string WindowsProfileName { get; set; } = string.Empty;
}
