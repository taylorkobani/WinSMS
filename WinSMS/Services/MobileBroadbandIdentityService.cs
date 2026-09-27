using System.Diagnostics;
using System.Text.RegularExpressions;

namespace WinSMS.Services;

/// <summary>
/// Reads the active mobile-broadband subscriber telephone number using the
/// Windows netsh MBN read-only diagnostics surface. This is used as a fallback
/// when SmsDevice2.AccountPhoneNumber is empty or stale after switching SIM/eSIM.
/// </summary>
public sealed class MobileBroadbandIdentityService
{
    private static readonly Regex PhoneLikeValue = new(
        @"\+?\d[\d\s().-]{5,}\d",
        RegexOptions.Compiled);

    public async Task<MobileBroadbandReadyInfo> GetReadyInfoAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var netsh = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "netsh.exe");

            var startInfo = new ProcessStartInfo
            {
                FileName = netsh,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("mbn");
            startInfo.ArgumentList.Add("show");
            startInfo.ArgumentList.Add("readyinfo");
            startInfo.ArgumentList.Add("*");

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return new MobileBroadbandReadyInfo(
                    Array.Empty<string>(),
                    string.Empty,
                    "Windows could not start netsh.");

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }

                return new MobileBroadbandReadyInfo(
                    Array.Empty<string>(),
                    string.Empty,
                    "Mobile broadband ready-info query timed out.");
            }

            var output = await outputTask;
            var error = await errorTask;

            var numbers = ParseTelephoneNumbers(output);

            return new MobileBroadbandReadyInfo(
                numbers,
                output,
                process.ExitCode == 0
                    ? null
                    : string.IsNullOrWhiteSpace(error)
                        ? $"netsh exited with code {process.ExitCode}."
                        : error.Trim());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new MobileBroadbandReadyInfo(
                Array.Empty<string>(),
                string.Empty,
                ex.Message);
        }
    }

    internal static IReadOnlyList<string> ParseTelephoneNumbers(string output)
    {
        var numbers = new List<string>();

        foreach (var rawLine in (output ?? string.Empty)
                     .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var line = rawLine.Trim();

            // Typical English netsh output is:
            // "Telephone #1 : +447..."
            // Ignore "Number of telephone numbers : 1".
            if (!line.Contains("telephone", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("number of telephone", StringComparison.OrdinalIgnoreCase))
                continue;

            var separator = line.IndexOf(':');
            if (separator < 0 || separator == line.Length - 1)
                continue;

            var value = line[(separator + 1)..].Trim();
            var match = PhoneLikeValue.Match(value);
            if (!match.Success)
                continue;

            var number = match.Value.Trim();
            var digits = new string(number.Where(char.IsDigit).ToArray());
            if (digits.Length < 7 || digits.Length > 15)
                continue;

            if (!numbers.Any(existing =>
                    Normalize(existing) == Normalize(number)))
            {
                numbers.Add(number);
            }
        }

        return numbers;
    }

    private static string Normalize(string number)
    {
        var digits = new string((number ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.StartsWith("00")) digits = digits[2..];
        if (digits.StartsWith("0") && digits.Length >= 10) digits = "44" + digits[1..];
        return digits;
    }
}

public sealed record MobileBroadbandReadyInfo(
    IReadOnlyList<string> TelephoneNumbers,
    string RawOutput,
    string? Error);
