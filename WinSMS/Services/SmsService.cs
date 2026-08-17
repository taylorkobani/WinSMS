using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WinSMS.Models;
using WinSMS.Services.Interfaces;

namespace WinSMS.Services;

public class SmsService : ISmsService
{
    private readonly IModemService _modem;
    private readonly IMessageArchiveService _archive;
    private readonly ILogger<SmsService> _logger;

    public event EventHandler<SmsMessage>? MessageReceived;

    public SmsService(IModemService modem, IMessageArchiveService archive, ILogger<SmsService> logger)
    {
        _modem = modem;
        _archive = archive;
        _logger = logger;

        _modem.UnsolicitedMessageReceived += OnUnsolicitedMessageReceived;
    }

    public async Task<IReadOnlyList<SmsMessage>> GetAllMessagesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _modem.SendCommandAsync("AT+CMGF=1", cancellationToken);
            var response = await _modem.SendCommandAsync("AT+CMGL=\"ALL\"", cancellationToken);
            return ParseCmglResponse(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve SMS messages");
            return Array.Empty<SmsMessage>();
        }
    }

    public async Task<IReadOnlyList<SmsMessage>> GetUnreadMessagesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _modem.SendCommandAsync("AT+CMGF=1", cancellationToken);
            var response = await _modem.SendCommandAsync("AT+CMGL=\"REC UNREAD\"", cancellationToken);
            return ParseCmglResponse(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve unread SMS messages");
            return Array.Empty<SmsMessage>();
        }
    }

    public async Task<SmsMessage?> GetMessageByIndexAsync(int index, CancellationToken cancellationToken = default)
    {
        try
        {
            await _modem.SendCommandAsync("AT+CMGF=1", cancellationToken);
            var response = await _modem.SendCommandAsync($"AT+CMGR={index}", cancellationToken);
            return ParseCmgrResponse(response, index);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve SMS message at index {Index}", index);
            return null;
        }
    }

    public async Task<SmsMessage> SendMessageAsync(string phoneNumber, string body, CancellationToken cancellationToken = default)
    {
        var message = new SmsMessage
        {
            PhoneNumber = phoneNumber,
            Body = body,
            Direction = SmsDirection.Outgoing,
            Status = SmsStatus.Pending,
            Timestamp = DateTimeOffset.Now
        };

        await _archive.SaveMessageAsync(message);

        try
        {
            message.Status = SmsStatus.Sending;
            await _archive.UpdateMessageAsync(message);

            await _modem.SendCommandAsync("AT+CMGF=1", cancellationToken);
            await _modem.SendCommandAsync($"AT+CMGS=\"{phoneNumber}\"", cancellationToken);

            // Send message body followed by Ctrl+Z (0x1A)
            var response = await _modem.SendCommandAsync(body + "\x1A", cancellationToken);

            if (response.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                message.Status = SmsStatus.Failed;
                message.Error = ExtractError(response);
                _logger.LogError("SMS send failed: {Error}", message.Error);
            }
            else
            {
                message.Status = SmsStatus.Sent;
                message.ModemReference = ExtractCmgsReference(response);
                _logger.LogInformation("SMS sent successfully to {PhoneNumber}", phoneNumber);
            }
        }
        catch (Exception ex)
        {
            message.Status = SmsStatus.Failed;
            message.Error = ex.Message;
            _logger.LogError(ex, "Failed to send SMS to {PhoneNumber}", phoneNumber);
        }
        finally
        {
            await _archive.UpdateMessageAsync(message);
        }

        return message;
    }

    public async Task DeleteMessageAsync(int modemIndex, CancellationToken cancellationToken = default)
    {
        try
        {
            await _modem.SendCommandAsync($"AT+CMGD={modemIndex}", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete SMS at index {Index}", modemIndex);
            throw;
        }
    }

    public Task MarkAsReadAsync(SmsMessage message)
    {
        message.IsRead = true;
        return _archive.UpdateMessageAsync(message);
    }

    private async void OnUnsolicitedMessageReceived(object? sender, string notification)
    {
        // Parse +CMTI: "SM",<index>
        var match = Regex.Match(notification, @"\+CMTI:\s*""?[^"",]+""?,\s*(\d+)");
        if (!match.Success) return;

        if (int.TryParse(match.Groups[1].Value, out var index))
        {
            try
            {
                var message = await GetMessageByIndexAsync(index);
                if (message != null)
                {
                    await _archive.SaveMessageAsync(message);
                    MessageReceived?.Invoke(this, message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve new SMS at index {Index}", index);
            }
        }
    }

    internal static IReadOnlyList<SmsMessage> ParseCmglResponse(string response)
    {
        var messages = new List<SmsMessage>();
        var lines = response.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < lines.Length - 1; i++)
        {
            // +CMGL: <index>,"<status>","<sender>",<alpha>,"<timestamp>"
            // alpha may be empty and unquoted, e.g. ,,
            var headerMatch = Regex.Match(lines[i],
                @"\+CMGL:\s*(\d+),""([^""]+)"",""([^""]*)"",([^,]*),""([^""]*)""");
            if (headerMatch.Success && i + 1 < lines.Length)
            {
                var status = headerMatch.Groups[2].Value;
                var direction = InferDirection(status);
                var msg = new SmsMessage
                {
                    ModemMessageIndex = int.Parse(headerMatch.Groups[1].Value),
                    PhoneNumber = headerMatch.Groups[3].Value,
                    Timestamp = ParseTimestamp(headerMatch.Groups[5].Value),
                    Direction = direction,
                    Status = direction == SmsDirection.Incoming ? SmsStatus.Received : SmsStatus.Sent,
                    IsRead = !status.Contains("UNREAD", StringComparison.OrdinalIgnoreCase)
                             && !status.Contains("UNSENT", StringComparison.OrdinalIgnoreCase),
                    Body = lines[i + 1].Trim()
                };
                messages.Add(msg);
                i++; // skip body line
            }
        }

        return messages;
    }

    private static SmsDirection InferDirection(string status)
    {
        // "REC READ" / "REC UNREAD" → Incoming
        // "STO SENT" / "STO UNSENT" → Outgoing
        if (status.StartsWith("STO", StringComparison.OrdinalIgnoreCase))
            return SmsDirection.Outgoing;
        return SmsDirection.Incoming;
    }

    internal static SmsMessage? ParseCmgrResponse(string response, int index)
    {
        var lines = response.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < lines.Length - 1; i++)
        {
            // +CMGR: "<status>","<sender>",<alpha>,"<timestamp>"
            var headerMatch = Regex.Match(lines[i],
                @"\+CMGR:\s*""([^""]+)"",""([^""]*)"",([^,]*),""([^""]*)""");
            if (headerMatch.Success)
            {
                return new SmsMessage
                {
                    ModemMessageIndex = index,
                    PhoneNumber = headerMatch.Groups[2].Value,
                    Timestamp = ParseTimestamp(headerMatch.Groups[4].Value),
                    Direction = SmsDirection.Incoming,
                    Status = SmsStatus.Received,
                    IsRead = !headerMatch.Groups[1].Value.Contains("UNREAD", StringComparison.OrdinalIgnoreCase),
                    Body = i + 1 < lines.Length ? lines[i + 1].Trim() : string.Empty
                };
            }
        }
        return null;
    }

    internal static DateTimeOffset ParseTimestamp(string timestamp)
    {
        // GSM timestamp format: yy/MM/dd,hh:mm:ss±zz
        // e.g. "26/08/17,13:00:00+04"
        if (string.IsNullOrWhiteSpace(timestamp))
            return DateTimeOffset.Now;

        try
        {
            var match = Regex.Match(timestamp, @"(\d{2})/(\d{2})/(\d{2}),(\d{2}):(\d{2}):(\d{2})([+-]\d{2})");
            if (match.Success)
            {
                var year = 2000 + int.Parse(match.Groups[1].Value);
                var month = int.Parse(match.Groups[2].Value);
                var day = int.Parse(match.Groups[3].Value);
                var hour = int.Parse(match.Groups[4].Value);
                var minute = int.Parse(match.Groups[5].Value);
                var second = int.Parse(match.Groups[6].Value);
                var tzQuarters = int.Parse(match.Groups[7].Value);
                var offset = TimeSpan.FromMinutes(tzQuarters * 15);
                return new DateTimeOffset(year, month, day, hour, minute, second, offset);
            }
        }
        catch
        {
            // Fall through to return current time
        }

        return DateTimeOffset.Now;
    }

    private static string? ExtractCmgsReference(string response)
    {
        var match = Regex.Match(response, @"\+CMGS:\s*(\d+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    internal static string? ExtractError(string response)
    {
        var cmeMatch = Regex.Match(response, @"\+CME ERROR:\s*(.+)");
        if (cmeMatch.Success) return $"CME ERROR: {cmeMatch.Groups[1].Value.Trim()}";

        var cmsMatch = Regex.Match(response, @"\+CMS ERROR:\s*(.+)");
        if (cmsMatch.Success) return $"CMS ERROR: {cmsMatch.Groups[1].Value.Trim()}";

        if (response.Contains("ERROR")) return "ERROR";

        return null;
    }
}
