using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using WinSMS.Models;
using WinSMS.Services.Interfaces;

namespace WinSMS.Services;

public class XmlMessageArchiveService : IMessageArchiveService
{
    private readonly ILogger<XmlMessageArchiveService> _logger;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private string _archiveDirectory;

    public XmlMessageArchiveService(ILogger<XmlMessageArchiveService> logger)
    {
        _logger = logger;
        _archiveDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinSMS", "Messages");
    }

    public void SetArchiveDirectory(string directory) => _archiveDirectory = directory;

    public async Task SaveMessageAsync(SmsMessage message)
    {
        await _fileLock.WaitAsync();
        try
        {
            EnsureDirectoryExists();

            var filePath = GetConversationFilePath(
                message.LocalSubscriptionId,
                message.LocalPhoneNumber,
                message.PhoneNumber);

            var doc = LoadOrCreateConversationDocument(
                filePath,
                message.LocalSubscriptionId,
                message.LocalPhoneNumber,
                message.PhoneNumber);

            var root = doc.Root!;
            var existing = root.Descendants("Message")
                .FirstOrDefault(e => e.Element("Id")?.Value == message.Id.ToString());

            if (existing != null)
                existing.ReplaceWith(MessageToXml(message));
            else
                root.Add(MessageToXml(message));

            await SaveDocumentAsync(doc, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save message to archive");
            throw;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<IReadOnlyList<SmsMessage>> LoadMessagesForDateAsync(DateOnly date)
    {
        var all = await LoadAllMessagesAsync();
        return all
            .Where(m => DateOnly.FromDateTime(m.Timestamp.LocalDateTime) == date)
            .OrderBy(m => m.Timestamp)
            .ToList();
    }

    public async Task<IReadOnlyList<SmsMessage>> LoadAllMessagesAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            EnsureDirectoryExists();
            var all = new List<SmsMessage>();

            foreach (var file in Directory.EnumerateFiles(_archiveDirectory, "*.xml"))
            {
                try
                {
                    all.AddRange(ParseMessages(XDocument.Load(file)));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read archive file {File}", file);
                }
            }

            return all.OrderBy(m => m.Timestamp).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load all messages");
            return Array.Empty<SmsMessage>();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task DeleteMessageAsync(Guid messageId)
    {
        await _fileLock.WaitAsync();
        try
        {
            EnsureDirectoryExists();

            foreach (var file in Directory.EnumerateFiles(_archiveDirectory, "*.xml"))
            {
                XDocument doc;
                try { doc = XDocument.Load(file); }
                catch { continue; }

                var element = doc.Descendants("Message")
                    .FirstOrDefault(e => e.Element("Id")?.Value == messageId.ToString());

                if (element == null)
                    continue;

                element.Remove();

                if (!doc.Descendants("Message").Any() &&
                    string.Equals(doc.Root?.Name.LocalName, "Conversation", StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
                else
                {
                    await SaveDocumentAsync(doc, file);
                }

                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete message {Id}", messageId);
            throw;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task UpdateMessageAsync(SmsMessage message)
    {
        await _fileLock.WaitAsync();
        var releaseLock = true;

        try
        {
            EnsureDirectoryExists();

            var preferredPath = GetConversationFilePath(
                message.LocalSubscriptionId,
                message.LocalPhoneNumber,
                message.PhoneNumber);

            var filePath = File.Exists(preferredPath)
                ? preferredPath
                : FindMessageFile(message.Id);

            if (filePath == null)
            {
                _fileLock.Release();
                releaseLock = false;
                await SaveMessageAsync(message);
                return;
            }

            var doc = XDocument.Load(filePath);
            var existing = doc.Descendants("Message")
                .FirstOrDefault(e => e.Element("Id")?.Value == message.Id.ToString());

            if (existing != null)
                existing.ReplaceWith(MessageToXml(message));
            else
                doc.Root!.Add(MessageToXml(message));

            await SaveDocumentAsync(doc, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update message {Id}", message.Id);
            throw;
        }
        finally
        {
            if (releaseLock)
                _fileLock.Release();
        }
    }

    public async Task<IReadOnlyList<SmsConversation>> LoadConversationsAsync(
        string localSubscriptionId,
        string? legacyLocalPhoneNumber = null)
    {
        var subscriptionKey = NormalizeSubscriptionId(localSubscriptionId);
        if (string.IsNullOrWhiteSpace(subscriptionKey))
            return Array.Empty<SmsConversation>();

        var all = await LoadAllMessagesAsync();

        return all
            .Where(message => MatchesLocalSubscription(
                message,
                subscriptionKey,
                legacyLocalPhoneNumber))
            .GroupBy(
                message => NormalizePhoneNumber(message.PhoneNumber),
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var ordered = group.OrderBy(message => message.Timestamp).ToList();
                var latest = ordered[^1];

                return new SmsConversation
                {
                    LocalSubscriptionId = localSubscriptionId,
                    LocalPhoneNumber = ordered
                        .Select(message => message.LocalPhoneNumber)
                        .LastOrDefault(value => !string.IsNullOrWhiteSpace(value))
                        ?? legacyLocalPhoneNumber
                        ?? string.Empty,
                    PhoneNumber = latest.PhoneNumber,
                    Messages = new ObservableCollection<SmsMessage>(ordered)
                };
            })
            .OrderByDescending(conversation => conversation.LastMessageTimestamp)
            .ToList();
    }

    public async Task<SmsConversation?> LoadConversationAsync(
        string localSubscriptionId,
        string phoneNumber,
        string? legacyLocalPhoneNumber = null)
    {
        var subscriptionKey = NormalizeSubscriptionId(localSubscriptionId);
        var remoteKey = NormalizePhoneNumber(phoneNumber);

        if (string.IsNullOrWhiteSpace(subscriptionKey) ||
            string.IsNullOrWhiteSpace(remoteKey))
        {
            return null;
        }

        var all = await LoadAllMessagesAsync();

        var messages = all
            .Where(message =>
                MatchesLocalSubscription(
                    message,
                    subscriptionKey,
                    legacyLocalPhoneNumber) &&
                NormalizePhoneNumber(message.PhoneNumber) == remoteKey)
            .OrderBy(message => message.Timestamp)
            .ToList();

        if (messages.Count == 0)
            return null;

        return new SmsConversation
        {
            LocalSubscriptionId = localSubscriptionId,
            LocalPhoneNumber = messages
                .Select(message => message.LocalPhoneNumber)
                .LastOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?? legacyLocalPhoneNumber
                ?? string.Empty,
            PhoneNumber = messages[^1].PhoneNumber,
            Messages = new ObservableCollection<SmsMessage>(messages)
        };
    }

    public async Task DeleteConversationAsync(
        string localSubscriptionId,
        string phoneNumber,
        string? legacyLocalPhoneNumber = null)
    {
        await _fileLock.WaitAsync();

        try
        {
            EnsureDirectoryExists();

            var subscriptionKey = NormalizeSubscriptionId(localSubscriptionId);
            var remoteKey = NormalizePhoneNumber(phoneNumber);

            foreach (var file in Directory.EnumerateFiles(_archiveDirectory, "*.xml").ToList())
            {
                XDocument doc;
                try { doc = XDocument.Load(file); }
                catch { continue; }

                var matches = doc.Descendants("Message")
                    .Where(element =>
                    {
                        var message = ParseMessageElement(element);
                        return message != null &&
                               MatchesLocalSubscription(
                                   message,
                                   subscriptionKey,
                                   legacyLocalPhoneNumber) &&
                               NormalizePhoneNumber(message.PhoneNumber) == remoteKey;
                    })
                    .ToList();

                if (matches.Count == 0)
                    continue;

                foreach (var element in matches)
                    element.Remove();

                if (!doc.Descendants("Message").Any() &&
                    string.Equals(doc.Root?.Name.LocalName, "Conversation", StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
                else
                {
                    await SaveDocumentAsync(doc, file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to delete conversation for subscription {SubscriptionId}, remote {PhoneNumber}",
                localSubscriptionId,
                phoneNumber);
            throw;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private string? FindMessageFile(Guid messageId)
    {
        foreach (var file in Directory.EnumerateFiles(_archiveDirectory, "*.xml"))
        {
            try
            {
                if (XDocument.Load(file)
                    .Descendants("Message")
                    .Any(e => e.Element("Id")?.Value == messageId.ToString()))
                {
                    return file;
                }
            }
            catch
            {
                // Ignore malformed legacy archive files here; normal archive
                // loading logs them separately.
            }
        }

        return null;
    }

    private static bool MatchesLocalSubscription(
        SmsMessage message,
        string subscriptionKey,
        string? legacyLocalPhoneNumber)
    {
        var messageSubscription = NormalizeSubscriptionId(message.LocalSubscriptionId);

        if (!string.IsNullOrWhiteSpace(messageSubscription))
            return messageSubscription == subscriptionKey;

        // Legacy archives predate ICCID ownership. They can be associated with
        // the current ICCID only when their old local phone number matches the
        // profile's phone number.
        var legacyKey = NormalizePhoneNumber(legacyLocalPhoneNumber ?? string.Empty);

        return !string.IsNullOrWhiteSpace(legacyKey) &&
               NormalizePhoneNumber(message.LocalPhoneNumber) == legacyKey;
    }

    private static XElement MessageToXml(SmsMessage msg)
    {
        var element = new XElement(
            "Message",
            new XElement("Id", msg.Id.ToString()),
            new XElement("Direction", msg.Direction.ToString()),
            new XElement("PhoneNumber", msg.PhoneNumber),
            new XElement("LocalSubscriptionId", msg.LocalSubscriptionId),
            new XElement("LocalPhoneNumber", msg.LocalPhoneNumber),
            new XElement("Body", msg.Body),
            new XElement("Timestamp", msg.Timestamp.ToString("O")),
            new XElement("Status", msg.Status.ToString()),
            new XElement("IsRead", msg.IsRead.ToString()));

        if (msg.ModemMessageIndex.HasValue)
            element.Add(new XElement("ModemMessageIndex", msg.ModemMessageIndex.Value));

        if (msg.ModemReference != null)
            element.Add(new XElement("ModemReference", msg.ModemReference));

        if (msg.Error != null)
            element.Add(new XElement("Error", msg.Error));

        return element;
    }

    internal static IReadOnlyList<SmsMessage> ParseMessages(XDocument doc)
        => doc.Descendants("Message")
            .Select(ParseMessageElement)
            .Where(message => message != null)
            .Cast<SmsMessage>()
            .ToList();

    internal static SmsMessage? ParseMessageElement(XElement element)
    {
        try
        {
            return new SmsMessage
            {
                Id = Guid.Parse(element.Element("Id")!.Value),
                Direction = Enum.Parse<SmsDirection>(element.Element("Direction")!.Value),
                PhoneNumber = element.Element("PhoneNumber")?.Value ?? string.Empty,
                LocalSubscriptionId =
                    element.Element("LocalSubscriptionId")?.Value ?? string.Empty,
                LocalPhoneNumber =
                    element.Element("LocalPhoneNumber")?.Value ?? string.Empty,
                Body = element.Element("Body")?.Value ?? string.Empty,
                Timestamp = DateTimeOffset.Parse(element.Element("Timestamp")!.Value),
                Status = Enum.Parse<SmsStatus>(element.Element("Status")!.Value),
                IsRead = bool.Parse(element.Element("IsRead")?.Value ?? "false"),
                ModemMessageIndex = element.Element("ModemMessageIndex") != null
                    ? int.Parse(element.Element("ModemMessageIndex")!.Value)
                    : null,
                ModemReference = element.Element("ModemReference")?.Value,
                Error = element.Element("Error")?.Value
            };
        }
        catch
        {
            return null;
        }
    }

    private static XDocument LoadOrCreateConversationDocument(
        string filePath,
        string localSubscriptionId,
        string localPhoneNumber,
        string phoneNumber)
    {
        if (File.Exists(filePath))
        {
            try { return XDocument.Load(filePath); }
            catch { }
        }

        return new XDocument(
            new XElement(
                "Conversation",
                new XAttribute("localSubscriptionId", localSubscriptionId ?? string.Empty),
                new XAttribute("localPhoneNumber", localPhoneNumber ?? string.Empty),
                new XAttribute("phoneNumber", phoneNumber ?? string.Empty),
                new XAttribute(
                    "subscriptionKey",
                    NormalizeSubscriptionId(localSubscriptionId)),
                new XAttribute(
                    "remoteKey",
                    NormalizePhoneNumber(phoneNumber))));
    }

    private string GetConversationFilePath(
        string localSubscriptionId,
        string localPhoneNumber,
        string phoneNumber)
    {
        var remoteKey = NormalizePhoneNumber(phoneNumber);
        var safeRemote = ToSafeKey(remoteKey, phoneNumber);

        if (!string.IsNullOrWhiteSpace(localSubscriptionId))
        {
            var normalized = NormalizeSubscriptionId(localSubscriptionId);
            var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];

            return Path.Combine(
                _archiveDirectory,
                $"conversation-sub-{hash}-{safeRemote}.xml");
        }

        // Preserve compatibility for messages created before ICCID ownership.
        var localKey = NormalizePhoneNumber(localPhoneNumber);
        var safeLocal = ToSafeKey(localKey, "unknown-local");

        return Path.Combine(
            _archiveDirectory,
            $"conversation-{safeLocal}-{safeRemote}.xml");
    }

    private static string ToSafeKey(string normalized, string fallback)
    {
        var safe = new string((normalized ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .ToArray());

        if (!string.IsNullOrWhiteSpace(safe))
            return safe;

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(fallback ?? string.Empty)))[..16];
    }

    private static string NormalizeSubscriptionId(string value)
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

    private static async Task SaveDocumentAsync(XDocument doc, string filePath)
    {
        var tmpPath = filePath + ".tmp";

        await using (var stream = new FileStream(
                         tmpPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None))
        {
            await Task.Run(() => doc.Save(stream));
        }

        File.Move(tmpPath, filePath, overwrite: true);
    }

    private void EnsureDirectoryExists()
    {
        if (!Directory.Exists(_archiveDirectory))
            Directory.CreateDirectory(_archiveDirectory);
    }
}
