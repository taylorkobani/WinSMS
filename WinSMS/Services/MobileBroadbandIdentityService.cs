using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;

namespace WinSMS.Services;

/// <summary>
/// Reads the active mobile-broadband subscriber identity through Windows'
/// read-only netsh MBN diagnostics. This supplements SmsDevice2 because some
/// dual-SIM/eSIM modems keep AccountPhoneNumber bound to the modem/SMS device
/// rather than the currently mapped SIM/eSIM subscription.
/// </summary>
public sealed class MobileBroadbandIdentityService
{
    private static readonly Regex PhoneLikeValue = new(
        @"\+?\d[\d\s().-]{5,}\d",
        RegexOptions.Compiled);

    private static readonly Regex InterfaceNameLine = new(
        @"^\s*Name\s*:\s*(.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex SlotIndexValue = new(
        @"slot(?:\s+index)?\s*[:=]?\s*(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<MobileBroadbandReadyInfo> GetReadyInfoAsync(
        CancellationToken cancellationToken = default)
    {
        var diagnostics = new StringBuilder();
        var errors = new List<string>();
        var numbers = new List<string>();

        try
        {
            var interfacesResult = await RunNetshAsync(
                cancellationToken, "mbn", "show", "interfaces");

            diagnostics.AppendLine("netsh mbn show interfaces:");
            diagnostics.AppendLine(interfacesResult.Output.Trim());

            if (!interfacesResult.Success && !string.IsNullOrWhiteSpace(interfacesResult.Error))
                errors.Add($"interfaces: {interfacesResult.Error}");

            var interfaceNames = ParseInterfaceNames(interfacesResult.Output).ToList();

            // Locale-independent fallback. WWANPP/WWANPP2 are the .NET network
            // interface types used for GSM/CDMA mobile-broadband interfaces.
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType is
                    NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2)
                {
                    if (!interfaceNames.Contains(nic.Name, StringComparer.OrdinalIgnoreCase))
                        interfaceNames.Add(nic.Name);
                }
            }

            if (interfaceNames.Count == 0)
            {
                return new MobileBroadbandReadyInfo(
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    diagnostics.ToString(),
                    errors.Count == 0
                        ? "Windows did not report a mobile broadband interface."
                        : string.Join(" | ", errors));
            }

            foreach (var interfaceName in interfaceNames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                diagnostics.AppendLine();
                diagnostics.AppendLine($"Interface: {interfaceName}");

                // The interface parameter is REQUIRED for "show readyinfo".
                // Calling "readyinfo *" is invalid and was the reason the
                // previous implementation always returned exit code 1.
                var ready = await RunNetshAsync(
                    cancellationToken,
                    "mbn", "show", "readyinfo", $"interface={interfaceName}");

                diagnostics.AppendLine("readyinfo:");
                diagnostics.AppendLine(ready.Output.Trim());

                AddUniqueNumbers(numbers, ParseTelephoneNumbers(ready.Output));

                if (!ready.Success)
                    errors.Add($"{interfaceName} readyinfo: {ready.Error ?? $"exit code {ready.ExitCode}"}");

                // On dual-SIM/eSIM hardware Windows exposes the currently mapped
                // modem slot separately. Query it read-only and, when we can
                // identify the slot index, ask readyinfo for that exact slot.
                var mapping = await RunNetshAsync(
                    cancellationToken,
                    "mbn", "show", "slotmapping", $"interface={interfaceName}");

                diagnostics.AppendLine("slotmapping:");
                diagnostics.AppendLine(mapping.Output.Trim());

                var activeSlotIndex = ParseMappedSlotIndex(mapping.Output);
                if (activeSlotIndex.HasValue)
                {
                    var mappedReady = await RunNetshAsync(
                        cancellationToken,
                        "mbn", "show", "readyinfo",
                        $"interface={interfaceName}",
                        $"slotindex={activeSlotIndex.Value}");

                    diagnostics.AppendLine($"readyinfo slotindex={activeSlotIndex.Value}:");
                    diagnostics.AppendLine(mappedReady.Output.Trim());

                    // Prefer the mapped slot's number by putting it first.
                    var mappedNumbers = ParseTelephoneNumbers(mappedReady.Output);
                    foreach (var mappedNumber in mappedNumbers.Reverse())
                    {
                        var normalized = Normalize(mappedNumber);
                        numbers.RemoveAll(existing => Normalize(existing) == normalized);
                        numbers.Insert(0, mappedNumber);
                    }
                }

                var slotStatus = await RunNetshAsync(
                    cancellationToken,
                    "mbn", "show", "slotstatus", $"interface={interfaceName}");

                diagnostics.AppendLine("slotstatus:");
                diagnostics.AppendLine(slotStatus.Output.Trim());
            }

            return new MobileBroadbandReadyInfo(
                numbers,
                interfaceNames,
                diagnostics.ToString(),
                errors.Count == 0 ? null : string.Join(" | ", errors));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new MobileBroadbandReadyInfo(
                numbers,
                Array.Empty<string>(),
                diagnostics.ToString(),
                ex.Message);
        }
    }

    internal static IReadOnlyList<string> ParseInterfaceNames(string output)
        => InterfaceNameLine.Matches(output ?? string.Empty)
            .Select(match => match.Groups[1].Value.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    internal static int? ParseMappedSlotIndex(string output)
    {
        foreach (var rawLine in (output ?? string.Empty)
                     .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (!line.Contains("slot", StringComparison.OrdinalIgnoreCase))
                continue;

            var match = SlotIndexValue.Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var slot))
                return slot;
        }

        return null;
    }

    internal static IReadOnlyList<string> ParseTelephoneNumbers(string output)
    {
        var numbers = new List<string>();

        foreach (var rawLine in (output ?? string.Empty)
                     .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var line = rawLine.Trim();

            // Typical English output:
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

            if (!numbers.Any(existing => Normalize(existing) == Normalize(number)))
                numbers.Add(number);
        }

        return numbers;
    }

    private static void AddUniqueNumbers(List<string> target, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            if (!target.Any(existing => Normalize(existing) == Normalize(value)))
                target.Add(value);
        }
    }

    private static async Task<NetshResult> RunNetshAsync(
        CancellationToken cancellationToken,
        params string[] arguments)
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

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            return new NetshResult(-1, string.Empty, "Windows could not start netsh.");

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
            return new NetshResult(-1, string.Empty, "netsh query timed out.");
        }

        var output = await outputTask;
        var error = await errorTask;

        return new NetshResult(
            process.ExitCode,
            output,
            string.IsNullOrWhiteSpace(error) ? null : error.Trim());
    }

    private static string Normalize(string number)
    {
        var digits = new string((number ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.StartsWith("00")) digits = digits[2..];
        if (digits.StartsWith("0") && digits.Length >= 10) digits = "44" + digits[1..];
        return digits;
    }

    private sealed record NetshResult(int ExitCode, string Output, string? Error)
    {
        public bool Success => ExitCode == 0;
    }
}

public sealed record MobileBroadbandReadyInfo(
    IReadOnlyList<string> TelephoneNumbers,
    IReadOnlyList<string> InterfaceNames,
    string RawOutput,
    string? Error);
