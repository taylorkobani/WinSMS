using System.Collections.ObjectModel;
using System.Xml.Linq;
using System.Security.Cryptography;
using System.Text;
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
            var filePath = GetConversationFilePath(message.PhoneNumber);
            var doc = LoadOrCreateConversationDocument(filePath, message.PhoneNumber);

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
        await _fileLock.WaitAsync();
        try
        {
            EnsureDirectoryExists();
            var all = new List<SmsMessage>();
            foreach (var file in Directory.EnumerateFiles(_archiveDirectory, "*.xml"))
            {
                try { all.AddRange(ParseMessages(XDocument.Load(file))); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to read archive file {File}", file); }
            }
            return all.Where(m => DateOnly.FromDateTime(m.Timestamp.LocalDateTime) == date)
                      .OrderBy(m => m.Timestamp).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load messages for date {Date}", date);
            return Array.Empty<SmsMessage>();
        }
        finally
        {
            _fileLock.Release();
        }
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
                    var doc = XDocument.Load(file);
                    all.AddRange(ParseMessages(doc));
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
            foreach (var file in Directory.EnumerateFiles(_archiveDirectory, "????-??-??.xml"))
            {
                var doc = XDocument.Load(file);
                var element = doc.Descendants("Message")
                    .FirstOrDefault(e => e.Element("Id")?.Value == messageId.ToString());

                if (element != null)
                {
                    element.Remove();
                    await SaveDocumentAsync(doc, file);
                    break;
                }
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
        bool released = false;
        try
        {
            var filePath = GetConversationFilePath(message.PhoneNumber);
            if (!File.Exists(filePath))
            {
                // During migration the message may still live in a legacy daily archive.
                var legacyFile = Directory.EnumerateFiles(_archiveDirectory, "????-??-??.xml")
                    .FirstOrDefault(f => XDocument.Load(f).Descendants("Message")
                        .Any(e => e.Element("Id")?.Value == message.Id.ToString()));
                if (legacyFile != null)
                    filePath = legacyFile;
                else
                {
                    _fileLock.Release();
                    released = true;
                    await SaveMessageAsync(message);
                    return;
                }
            }

            var doc = XDocument.Load(filePath);
            var existing = doc.Descendants("Message")
                .FirstOrDefault(e => e.Element("Id")?.Value == message.Id.ToString());

            if (existing != null)
            {
                existing.ReplaceWith(MessageToXml(message));
            }
            else
            {
                doc.Root!.Add(MessageToXml(message));
            }

            await SaveDocumentAsync(doc, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update message {Id}", message.Id);
            throw;
        }
        finally
        {
            if (!released)
                _fileLock.Release();
        }
    }

    private static XElement MessageToXml(SmsMessage msg)
    {
        var el = new XElement("Message",
            new XElement("Id", msg.Id.ToString()),
            new XElement("Direction", msg.Direction.ToString()),
            new XElement("PhoneNumber", msg.PhoneNumber),
            new XElement("Body", msg.Body),
            new XElement("Timestamp", msg.Timestamp.ToString("O")),
            new XElement("Status", msg.Status.ToString()),
            new XElement("IsRead", msg.IsRead.ToString()));

        if (msg.ModemMessageIndex.HasValue)
            el.Add(new XElement("ModemMessageIndex", msg.ModemMessageIndex.Value));
        if (msg.ModemReference != null)
            el.Add(new XElement("ModemReference", msg.ModemReference));
        if (msg.Error != null)
            el.Add(new XElement("Error", msg.Error));

        return el;
    }

    internal static IReadOnlyList<SmsMessage> ParseMessages(XDocument doc)
    {
        return doc.Descendants("Message").Select(ParseMessageElement).Where(m => m != null).Cast<SmsMessage>().ToList();
    }

    internal static SmsMessage? ParseMessageElement(XElement el)
    {
        try
        {
            return new SmsMessage
            {
                Id = Guid.Parse(el.Element("Id")!.Value),
                Direction = Enum.Parse<SmsDirection>(el.Element("Direction")!.Value),
                PhoneNumber = el.Element("PhoneNumber")?.Value ?? string.Empty,
                Body = el.Element("Body")?.Value ?? string.Empty,
                Timestamp = DateTimeOffset.Parse(el.Element("Timestamp")!.Value),
                Status = Enum.Parse<SmsStatus>(el.Element("Status")!.Value),
                IsRead = bool.Parse(el.Element("IsRead")?.Value ?? "false"),
                ModemMessageIndex = el.Element("ModemMessageIndex") != null
                    ? int.Parse(el.Element("ModemMessageIndex")!.Value) : null,
                ModemReference = el.Element("ModemReference")?.Value,
                Error = el.Element("Error")?.Value
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<SmsConversation>> LoadConversationsAsync()
    {
        var all = await LoadAllMessagesAsync();
        return all.GroupBy(m => NormalizePhoneNumber(m.PhoneNumber), StringComparer.OrdinalIgnoreCase)
            .Select(g => new SmsConversation
            {
                PhoneNumber = g.OrderByDescending(m => m.Timestamp).First().PhoneNumber,
                Messages = new ObservableCollection<SmsMessage>(g.OrderBy(m => m.Timestamp))
            })
            .OrderByDescending(c => c.LastMessageTimestamp)
            .ToList();
    }

    public async Task<SmsConversation?> LoadConversationAsync(string phoneNumber)
    {
        var key = NormalizePhoneNumber(phoneNumber);
        var all = await LoadAllMessagesAsync();
        var messages = all.Where(m => NormalizePhoneNumber(m.PhoneNumber) == key)
                          .OrderBy(m => m.Timestamp).ToList();
        return messages.Count == 0 ? null : new SmsConversation
        {
            PhoneNumber = messages[^1].PhoneNumber,
            Messages = new ObservableCollection<SmsMessage>(messages)
        };
    }

    public async Task DeleteConversationAsync(string phoneNumber)
    {
        await _fileLock.WaitAsync();
        try
        {
            EnsureDirectoryExists();
            var key = NormalizePhoneNumber(phoneNumber);

            // Remove the dedicated conversation file used by the current format.
            var conversationFile = GetConversationFilePath(phoneNumber);
            if (File.Exists(conversationFile))
                File.Delete(conversationFile);

            // Also remove this number from any legacy daily XML files so an old
            // message cannot recreate the conversation on the next refresh.
            foreach (var file in Directory.EnumerateFiles(_archiveDirectory, "????-??-??.xml"))
            {
                var doc = XDocument.Load(file);
                var matches = doc.Descendants("Message")
                    .Where(e => NormalizePhoneNumber(e.Element("PhoneNumber")?.Value ?? string.Empty) == key)
                    .ToList();

                if (matches.Count == 0) continue;

                foreach (var element in matches)
                    element.Remove();

                await SaveDocumentAsync(doc, file);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete conversation for {PhoneNumber}", phoneNumber);
            throw;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private static XDocument LoadOrCreateConversationDocument(string filePath, string phoneNumber)
    {
        if (File.Exists(filePath))
        {
            try { return XDocument.Load(filePath); }
            catch { }
        }
        return new XDocument(new XElement("Conversation",
            new XAttribute("phoneNumber", phoneNumber),
            new XAttribute("key", NormalizePhoneNumber(phoneNumber))));
    }

    private string GetConversationFilePath(string phoneNumber)
    {
        var key = NormalizePhoneNumber(phoneNumber);
        var safe = new string(key.Where(char.IsDigit).ToArray());
        if (string.IsNullOrWhiteSpace(safe))
            safe = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(phoneNumber)))[..16];
        return Path.Combine(_archiveDirectory, $"conversation-{safe}.xml");
    }

    private static string NormalizePhoneNumber(string phoneNumber)
    {
        var trimmed = phoneNumber?.Trim() ?? string.Empty;
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("00")) digits = digits[2..];
        if (digits.StartsWith("0") && digits.Length >= 10) digits = "44" + digits[1..];
        return digits;
    }

    private static async Task SaveDocumentAsync(XDocument doc, string filePath)
    {
        var tmpPath = filePath + ".tmp";
        await using var stream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await Task.Run(() => doc.Save(stream));
        stream.Close();
        File.Move(tmpPath, filePath, overwrite: true);
    }

    private string GetFilePath(DateOnly date)
        => Path.Combine(_archiveDirectory, $"{date:yyyy-MM-dd}.xml");

    private void EnsureDirectoryExists()
    {
        if (!Directory.Exists(_archiveDirectory))
            Directory.CreateDirectory(_archiveDirectory);
    }
}
