using System.Text.Json;

namespace WinSMS.Services;

public sealed class BlockedNumberService
{
    private readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WinSMS", "blocked-numbers.json");

    private readonly object _sync = new();
    private HashSet<string>? _blocked;

    public bool IsBlocked(string phoneNumber)
    {
        var key = NormalizePhoneNumber(phoneNumber);
        if (string.IsNullOrWhiteSpace(key)) return false;
        lock (_sync) return GetBlocked().Contains(key);
    }

    public async Task<bool> ToggleAsync(string phoneNumber)
    {
        var key = NormalizePhoneNumber(phoneNumber);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Cannot block an empty phone number.");

        HashSet<string> snapshot;
        bool blocked;
        lock (_sync)
        {
            var numbers = GetBlocked();
            blocked = numbers.Contains(key) ? !numbers.Remove(key) : numbers.Add(key);
            snapshot = new HashSet<string>(numbers, StringComparer.OrdinalIgnoreCase);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        await File.WriteAllTextAsync(_filePath,
            JsonSerializer.Serialize(snapshot.OrderBy(x => x),
                new JsonSerializerOptions { WriteIndented = true }));
        return blocked;
    }

    private HashSet<string> GetBlocked()
    {
        if (_blocked != null) return _blocked;
        try
        {
            if (File.Exists(_filePath))
            {
                var values = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_filePath)) ?? new();
                _blocked = new HashSet<string>(values.Select(NormalizePhoneNumber)
                    .Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
                return _blocked;
            }
        }
        catch { }

        return _blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizePhoneNumber(string phoneNumber)
    {
        var digits = new string((phoneNumber ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.StartsWith("00")) digits = digits[2..];
        if (digits.StartsWith("0") && digits.Length >= 10) digits = "44" + digits[1..];
        return digits;
    }
}
