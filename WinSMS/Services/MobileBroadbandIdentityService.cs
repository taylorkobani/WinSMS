using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;

namespace WinSMS.Services;

/// <summary>
/// Reads Windows Mobile Broadband subscriber/slot information and performs an
/// explicit slot-mapping change when the user requests a SIM/eSIM switch. On
/// multi-SIM/eSIM systems SmsDevice2.AccountPhoneNumber can be bound to the SMS
/// device while Windows routes traffic through another slot.
/// </summary>
public sealed class MobileBroadbandIdentityService
{
    private readonly LegacyMbnSubscriberService _legacyMbn;

    public MobileBroadbandIdentityService(LegacyMbnSubscriberService legacyMbn)
    {
        _legacyMbn = legacyMbn;
    }

    private static readonly Regex PhoneLikeValue = new(
        @"\+?\d[\d\s().-]{5,}\d",
        RegexOptions.Compiled);

    private static readonly Regex InterfaceNameLine = new(
        @"^\s*Name\s*:\s*(.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex SlotIndexRegex = new(
        @"slot\s+index\s+(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IccIdRegex = new(
        @"(?:ICCID|SIM\s+ICC(?:\s*ID)?)\s*:\s*([0-9A-F]{15,22})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ProfileNameRegex = new(
        @"^\s*Profile(?:\s+name)?\s*:\s*(.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex ProfilesListRegex = new(
        @"^\s*(?:All\s+User\s+Profile|Profile\s+Name|Profile)\s*:\s*(.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    public async Task<MobileBroadbandReadyInfo> GetReadyInfoAsync(
        CancellationToken cancellationToken = default)
    {
        var diagnostics = new StringBuilder();
        var errors = new List<string>();
        var interfaceNames = new List<string>();
        var slotResults = new List<MobileBroadbandSlotInfo>();
        var windowsProfileName = string.Empty;
        IReadOnlyList<LegacyMbnSubscriberInfo> legacySubscribers = Array.Empty<LegacyMbnSubscriberInfo>();

        try
        {
            try
            {
                legacySubscribers = await Task.Run(
                    () => _legacyMbn.GetSubscribers(),
                    cancellationToken);

                diagnostics.AppendLine("Win32 MBN subscriber information:");
                foreach (var subscriber in legacySubscribers)
                {
                    diagnostics.AppendLine($"Interface ID: {subscriber.InterfaceId}");
                    diagnostics.AppendLine($"SIM ICCID: {(string.IsNullOrWhiteSpace(subscriber.SimIccId) ? "(not reported)" : subscriber.SimIccId)}");
                    diagnostics.AppendLine($"Subscriber ID: {(string.IsNullOrWhiteSpace(subscriber.SubscriberId) ? "(not reported)" : subscriber.SubscriberId)}");
                    diagnostics.AppendLine($"Telephone number(s): {(subscriber.TelephoneNumbers.Count == 0 ? "(not reported)" : string.Join(", ", subscriber.TelephoneNumbers))}");
                    if (!string.IsNullOrWhiteSpace(subscriber.Error))
                        diagnostics.AppendLine($"MBN API error: {subscriber.Error}");
                }

                diagnostics.AppendLine();
            }
            catch (Exception ex)
            {
                errors.Add($"Win32 MBN API: {ex.Message}");
            }
            var interfacesResult = await RunNetshAsync(
                cancellationToken, "mbn", "show", "interfaces");

            diagnostics.AppendLine("netsh mbn show interfaces:");
            AppendResult(diagnostics, interfacesResult);

            interfaceNames.AddRange(ParseInterfaceNames(interfacesResult.Output));

            // Locale-independent fallback to Windows WWAN adapters.
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
                var legacyNumbersOnly = legacySubscribers
                    .SelectMany(subscriber => subscriber.TelephoneNumbers)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new MobileBroadbandReadyInfo(
                    legacyNumbersOnly,
                    Array.Empty<string>(),
                    Array.Empty<MobileBroadbandSlotInfo>(),
                    null,
                    legacySubscribers,
                    string.Empty,
                    diagnostics.ToString(),
                    "Windows did not report a mobile broadband interface.");
            }

            foreach (var interfaceName in interfaceNames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                diagnostics.AppendLine();
                diagnostics.AppendLine($"===== Interface: {interfaceName} =====");

                var connection = await RunNetshAsync(
                    cancellationToken,
                    "mbn", "show", "connection", $"interface={interfaceName}");

                diagnostics.AppendLine("connection:");
                AppendResult(diagnostics, connection);

                if (string.IsNullOrWhiteSpace(windowsProfileName))
                    windowsProfileName = ParseConnectionProfileName(connection.Output);

                if (string.IsNullOrWhiteSpace(windowsProfileName))
                {
                    var profiles = await RunNetshAsync(
                        cancellationToken,
                        "mbn", "show", "profiles", $"interface={interfaceName}");

                    diagnostics.AppendLine("profiles:");
                    AppendResult(diagnostics, profiles);

                    windowsProfileName = ParseProfileNames(profiles.Output)
                        .FirstOrDefault() ?? string.Empty;
                }

                var slotStatus = await RunNetshAsync(
                    cancellationToken,
                    "mbn", "show", "slotstatus", $"interface={interfaceName}");

                diagnostics.AppendLine("slotstatus:");
                AppendResult(diagnostics, slotStatus);

                var slotMapping = await RunNetshAsync(
                    cancellationToken,
                    "mbn", "show", "slotmapping", $"interface={interfaceName}");

                diagnostics.AppendLine("slotmapping:");
                AppendResult(diagnostics, slotMapping);

                var slotIndexes = ParseSlotIndexes(slotStatus.Output)
                    .Concat(ParseSlotIndexes(slotMapping.Output))
                    .Distinct()
                    .OrderBy(index => index)
                    .ToList();

                // Some drivers expose the slots but omit them from one or both
                // netsh reports. Probe the standard DSSA indexes as a safe,
                // read-only fallback.
                if (slotIndexes.Count == 0)
                {
                    slotIndexes.Add(0);
                    slotIndexes.Add(1);
                }

                var mappedSlot = ParseMappedSlotIndex(slotMapping.Output);
                var activeFromStatus = ParseActiveSlotIndex(slotStatus.Output);
                var selectedSlot = mappedSlot ?? activeFromStatus;

                foreach (var slotIndex in slotIndexes)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var ready = await RunNetshAsync(
                        cancellationToken,
                        "mbn", "show", "readyinfo",
                        $"interface={interfaceName}",
                        $"slotindex={slotIndex}");

                    diagnostics.AppendLine($"readyinfo slotindex={slotIndex}:");
                    AppendResult(diagnostics, ready);

                    var numbers = ParseTelephoneNumbers(ready.Output);
                    var iccId = ParseIccId(ready.Output);
                    var stateText = GetSlotStateText(slotStatus.Output, slotIndex);
                    var isEsim = stateText.Contains("esim", StringComparison.OrdinalIgnoreCase);

                    slotResults.Add(new MobileBroadbandSlotInfo(
                        interfaceName,
                        slotIndex,
                        selectedSlot == slotIndex,
                        isEsim,
                        iccId,
                        stateText,
                        numbers,
                        ready.ExitCode,
                        ready.Error));
                }

                // Also try the documented interface-only form and the wildcard
                // form because modem/WWAN driver implementations differ.
                foreach (var readyArgs in new[]
                {
                    new[] { "mbn", "show", "readyinfo", $"interface={interfaceName}" },
                    new[] { "mbn", "show", "readyinfo", "interface=*" }
                })
                {
                    var ready = await RunNetshAsync(cancellationToken, readyArgs);
                    diagnostics.AppendLine($"readyinfo {string.Join(" ", readyArgs.Skip(3))}:");
                    AppendResult(diagnostics, ready);

                    if (!ready.Success && !string.IsNullOrWhiteSpace(ready.Error))
                        errors.Add($"{interfaceName} readyinfo: {ready.Error}");
                }
            }

            var currentIccId = legacySubscribers
                .Select(subscriber => subscriber.SimIccId)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?? string.Empty;

            var selected = !string.IsNullOrWhiteSpace(currentIccId)
                ? slotResults.FirstOrDefault(slot =>
                    NormalizeIccId(slot.IccId) == NormalizeIccId(currentIccId))
                : null;

            selected ??= slotResults.FirstOrDefault(slot => slot.IsSelected);

            // A freshly enumerated Win32 MBN interface is the most direct
            // subscriber source Windows exposes to ordinary desktop apps. Use
            // its telephone numbers before the netsh fallback. Microsoft
            // specifically warns not to cache IMbnInterface objects because
            // cached functional objects can return stale subscriber data.
            var preferredNumbers = legacySubscribers
                .Where(subscriber => string.IsNullOrWhiteSpace(subscriber.Error))
                .SelectMany(subscriber => subscriber.TelephoneNumbers)
                .Where(number => !string.IsNullOrWhiteSpace(number))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (selected != null)
            {
                foreach (var selectedNumber in selected.TelephoneNumbers.Reverse())
                {
                    var normalized = Normalize(selectedNumber);
                    preferredNumbers.RemoveAll(existing => Normalize(existing) == normalized);
                    preferredNumbers.Insert(0, selectedNumber);
                }
            }

            foreach (var slot in slotResults)
            {
                foreach (var number in slot.TelephoneNumbers)
                {
                    if (!preferredNumbers.Any(existing =>
                        Normalize(existing) == Normalize(number)))
                    {
                        preferredNumbers.Add(number);
                    }
                }
            }

            return new MobileBroadbandReadyInfo(
                preferredNumbers,
                interfaceNames,
                slotResults,
                selected,
                legacySubscribers,
                windowsProfileName,
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
                legacySubscribers.SelectMany(subscriber => subscriber.TelephoneNumbers).ToList(),
                interfaceNames,
                slotResults,
                slotResults.FirstOrDefault(slot => slot.IsSelected),
                legacySubscribers,
                windowsProfileName,
                diagnostics.ToString(),
                ex.Message);
        }
    }

    public async Task<MobileBroadbandSwitchResult> SwitchSlotAsync(
        bool useEsim,
        CancellationToken cancellationToken = default)
    {
        // Use only interface + slot-status queries here. The full ready-info
        // diagnostic is intentionally avoided because some modem drivers reject
        // readyinfo and it should not delay a user-initiated slot switch.
        var interfacesResult = await RunNetshAsync(
            cancellationToken,
            "mbn",
            "show",
            "interfaces");

        var interfaceNames = ParseInterfaceNames(interfacesResult.Output).ToList();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is
                NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2)
            {
                if (!interfaceNames.Contains(nic.Name, StringComparer.OrdinalIgnoreCase))
                    interfaceNames.Add(nic.Name);
            }
        }

        foreach (var interfaceName in interfaceNames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var slotStatus = await RunNetshAsync(
                cancellationToken,
                "mbn",
                "show",
                "slotstatus",
                $"interface={interfaceName}");

            var targetSlot = ParseSlotIndexes(slotStatus.Output)
                .Select(index => new
                {
                    Index = index,
                    State = GetSlotStateText(slotStatus.Output, index)
                })
                .FirstOrDefault(slot =>
                    slot.State.Contains("esim", StringComparison.OrdinalIgnoreCase) == useEsim);

            if (targetSlot == null)
                continue;

            var normal = await RunNetshAsync(
                cancellationToken,
                "mbn",
                "set",
                "slotmapping",
                $"interface={interfaceName}",
                $"slotindex={targetSlot.Index}");

            if (normal.Success)
            {
                return new MobileBroadbandSwitchResult(
                    true,
                    interfaceName,
                    targetSlot.Index,
                    false,
                    null);
            }

            var elevated = await RunElevatedNetshAsync(
                cancellationToken,
                "mbn",
                "set",
                "slotmapping",
                $"interface={interfaceName}",
                $"slotindex={targetSlot.Index}");

            if (elevated.Success)
            {
                return new MobileBroadbandSwitchResult(
                    true,
                    interfaceName,
                    targetSlot.Index,
                    true,
                    null);
            }

            // Some WWAN drivers/netsh builds have been observed to return a
            // non-zero exit code even when the slot mapping has already changed.
            // Verify the actual mapping before reporting failure.
            var mappingAfterAttempt = await RunNetshAsync(
                cancellationToken,
                "mbn",
                "show",
                "slotmapping",
                $"interface={interfaceName}");

            var mappedAfterAttempt =
                ParseMappedSlotIndex(mappingAfterAttempt.Output);

            if (mappedAfterAttempt == targetSlot.Index)
            {
                return new MobileBroadbandSwitchResult(
                    true,
                    interfaceName,
                    targetSlot.Index,
                    elevated.WasElevated,
                    null);
            }

            var normalDetail = FirstUsefulText(normal.Error, normal.Output);
            var mappingDetail = FirstUsefulText(
                mappingAfterAttempt.Error,
                mappingAfterAttempt.Output);

            var error = new StringBuilder();
            error.Append(
                $"Windows could not map {interfaceName} to slot {targetSlot.Index}. ");

            if (!string.IsNullOrWhiteSpace(normalDetail))
                error.Append($"netsh: {normalDetail} ");

            if (!string.IsNullOrWhiteSpace(elevated.Error))
                error.Append($"Elevated attempt: {elevated.Error} ");

            if (!string.IsNullOrWhiteSpace(mappingDetail))
                error.Append($"Current mapping: {mappingDetail}");

            return new MobileBroadbandSwitchResult(
                false,
                interfaceName,
                targetSlot.Index,
                elevated.WasElevated,
                error.ToString().Trim());
        }

        return new MobileBroadbandSwitchResult(
            false,
            string.Empty,
            -1,
            false,
            useEsim
                ? "Windows did not report an eSIM slot that WinSMS can activate."
                : "Windows did not report a physical SIM slot that WinSMS can activate.");
    }

    internal static IReadOnlyList<string> ParseInterfaceNames(string output)
        => InterfaceNameLine.Matches(output ?? string.Empty)
            .Select(match => match.Groups[1].Value.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    internal static IReadOnlyList<int> ParseSlotIndexes(string output)
        => SlotIndexRegex.Matches(output ?? string.Empty)
            .Select(match => int.TryParse(match.Groups[1].Value, out var index)
                ? (int?)index
                : null)
            .Where(index => index.HasValue)
            .Select(index => index!.Value)
            .Distinct()
            .ToList();

    internal static int? ParseMappedSlotIndex(string output)
    {
        foreach (var rawLine in SplitLines(output))
        {
            var line = rawLine.Trim();
            if (!line.Contains("slot", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!line.Contains("map", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("select", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("default", StringComparison.OrdinalIgnoreCase))
                continue;

            var match = SlotIndexRegex.Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var index))
                return index;

            // Some versions output just "Slot mapping : 1".
            var colon = line.LastIndexOf(':');
            if (colon >= 0 &&
                int.TryParse(line[(colon + 1)..].Trim(), out index))
                return index;
        }

        return null;
    }

    internal static int? ParseActiveSlotIndex(string output)
    {
        foreach (var rawLine in SplitLines(output))
        {
            var line = rawLine.Trim();
            if (!line.Contains("slot index", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!line.Contains("active", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("available", StringComparison.OrdinalIgnoreCase))
                continue;

            var match = SlotIndexRegex.Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var index))
                return index;
        }

        return null;
    }

    internal static string ParseIccId(string output)
    {
        var match = IccIdRegex.Match(output ?? string.Empty);
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    internal static string ParseConnectionProfileName(string output)
    {
        var match = ProfileNameRegex.Match(output ?? string.Empty);
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    internal static IReadOnlyList<string> ParseProfileNames(string output)
        => ProfilesListRegex.Matches(output ?? string.Empty)
            .Select(match => match.Groups[1].Value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    internal static IReadOnlyList<string> ParseTelephoneNumbers(string output)
    {
        var numbers = new List<string>();

        foreach (var rawLine in SplitLines(output))
        {
            var line = rawLine.Trim();

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

    private static string GetSlotStateText(string output, int slotIndex)
    {
        foreach (var rawLine in SplitLines(output))
        {
            var line = rawLine.Trim();
            if (line.Contains($"slot index {slotIndex}", StringComparison.OrdinalIgnoreCase))
                return line;
        }

        return string.Empty;
    }

    private static IEnumerable<string> SplitLines(string? output)
        => (output ?? string.Empty)
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

    private static void AppendResult(StringBuilder diagnostics, NetshResult result)
    {
        diagnostics.AppendLine($"Exit code: {result.ExitCode}");
        if (!string.IsNullOrWhiteSpace(result.Output))
            diagnostics.AppendLine(result.Output.Trim());
        if (!string.IsNullOrWhiteSpace(result.Error))
            diagnostics.AppendLine($"stderr: {result.Error}");
    }

    private static async Task<ElevatedNetshResult> RunElevatedNetshAsync(
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var netsh = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "netsh.exe");

        var startInfo = new ProcessStartInfo
        {
            FileName = netsh,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return new ElevatedNetshResult(
                    false,
                    true,
                    "Windows could not start the elevated SIM/eSIM switch command.");
            }

            await process.WaitForExitAsync(cancellationToken);

            return process.ExitCode == 0
                ? new ElevatedNetshResult(true, true, null)
                : new ElevatedNetshResult(
                    false,
                    true,
                    $"Elevated netsh exited with code {process.ExitCode}.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new ElevatedNetshResult(
                false,
                true,
                "The SIM/eSIM switch was cancelled at the Windows administrator prompt.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ElevatedNetshResult(false, true, ex.Message);
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

        return new NetshResult(
            process.ExitCode,
            await outputTask,
            string.IsNullOrWhiteSpace(await errorTask) ? null : (await errorTask).Trim());
    }

    private static string? FirstUsefulText(params string?[] values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var compact = string.Join(
                " ",
                value.Split(
                    new[] { "\r\n", "\n" },
                    StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .Where(line => !string.IsNullOrWhiteSpace(line)));

            if (!string.IsNullOrWhiteSpace(compact))
                return compact;
        }

        return null;
    }

    private static string NormalizeIccId(string value)
        => new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

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

    private sealed record ElevatedNetshResult(
        bool Success,
        bool WasElevated,
        string? Error);
}

public sealed record MobileBroadbandSlotInfo(
    string InterfaceName,
    int SlotIndex,
    bool IsSelected,
    bool IsEsim,
    string IccId,
    string State,
    IReadOnlyList<string> TelephoneNumbers,
    int ReadyInfoExitCode,
    string? ReadyInfoError);

public sealed record MobileBroadbandReadyInfo(
    IReadOnlyList<string> TelephoneNumbers,
    IReadOnlyList<string> InterfaceNames,
    IReadOnlyList<MobileBroadbandSlotInfo> Slots,
    MobileBroadbandSlotInfo? SelectedSlot,
    IReadOnlyList<LegacyMbnSubscriberInfo> LegacySubscribers,
    string WindowsProfileName,
    string RawOutput,
    string? Error);


public sealed record MobileBroadbandSwitchResult(
    bool Success,
    string InterfaceName,
    int SlotIndex,
    bool UsedElevation,
    string? Error);
