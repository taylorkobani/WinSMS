using System.Xml.Linq;
using WinSMS.Helpers;
using WinSMS.Models;
using WinSMS.Services;
using Xunit;

namespace WinSMS.Tests;

public class PhoneNumberValidationTests
{
    [Theory]
    [InlineData("+447700900000", true)]
    [InlineData("+12125551234", true)]
    [InlineData("+35312345678", true)]
    [InlineData("07700900000", true)]  // starts with 0 but >= 7 digits
    [InlineData("+1234567", true)]     // min length international
    [InlineData("", false)]
    [InlineData("+123", false)]        // too short
    [InlineData("notanumber", false)]
    [InlineData("+", false)]
    public void IsValidPhoneNumber_VariousInputs(string number, bool expected)
    {
        var result = PhoneNumberHelper.IsValidPhoneNumber(number);
        Assert.Equal(expected, result);
    }
}

public class XmlArchiveParsingTests
{
    [Fact]
    public void ParseMessageElement_RoundTrip()
    {
        var original = new SmsMessage
        {
            Id = Guid.NewGuid(),
            PhoneNumber = "+447700900001",
            LocalSubscriptionId = "8944200203685005190F",
            LocalPhoneNumber = "+447309320937",
            Body = "Test message",
            Timestamp = new DateTimeOffset(2026, 8, 17, 13, 0, 0, TimeSpan.FromHours(1)),
            Direction = SmsDirection.Incoming,
            Status = SmsStatus.Received,
            IsRead = false,
            ModemMessageIndex = 3
        };

        var xml = new XElement("Message",
            new XElement("Id", original.Id.ToString()),
            new XElement("Direction", original.Direction.ToString()),
            new XElement("PhoneNumber", original.PhoneNumber),
            new XElement("LocalSubscriptionId", original.LocalSubscriptionId),
            new XElement("LocalPhoneNumber", original.LocalPhoneNumber),
            new XElement("Body", original.Body),
            new XElement("Timestamp", original.Timestamp.ToString("O")),
            new XElement("Status", original.Status.ToString()),
            new XElement("IsRead", original.IsRead.ToString()),
            new XElement("ModemMessageIndex", original.ModemMessageIndex.Value));

        var parsed = XmlMessageArchiveService.ParseMessageElement(xml);

        Assert.NotNull(parsed);
        Assert.Equal(original.Id, parsed.Id);
        Assert.Equal(original.PhoneNumber, parsed.PhoneNumber);
        Assert.Equal(original.LocalSubscriptionId, parsed.LocalSubscriptionId);
        Assert.Equal(original.LocalPhoneNumber, parsed.LocalPhoneNumber);
        Assert.Equal(original.Body, parsed.Body);
        Assert.Equal(original.Direction, parsed.Direction);
        Assert.Equal(original.Status, parsed.Status);
        Assert.Equal(original.IsRead, parsed.IsRead);
        Assert.Equal(original.ModemMessageIndex, parsed.ModemMessageIndex);
    }

    [Fact]
    public void ParseMessageElement_MissingOptionalFields_ReturnsMessage()
    {
        var xml = new XElement("Message",
            new XElement("Id", Guid.NewGuid().ToString()),
            new XElement("Direction", "Outgoing"),
            new XElement("PhoneNumber", "+12125551234"),
            new XElement("Body", "Hello"),
            new XElement("Timestamp", DateTimeOffset.Now.ToString("O")),
            new XElement("Status", "Sent"),
            new XElement("IsRead", "True"));

        var parsed = XmlMessageArchiveService.ParseMessageElement(xml);
        Assert.NotNull(parsed);
        Assert.Null(parsed.ModemMessageIndex);
        Assert.Null(parsed.Error);
    }

    [Fact]
    public void ParseMessageElement_InvalidXml_ReturnsNull()
    {
        var xml = new XElement("Message",
            new XElement("Id", "not-a-guid"));
        var parsed = XmlMessageArchiveService.ParseMessageElement(xml);
        Assert.Null(parsed);
    }

    [Fact]
    public void ParseMessages_MultipleMessages()
    {
        var doc = XDocument.Parse(@"
<Messages date=""2026-08-17"">
  <Message>
    <Id>" + Guid.NewGuid() + @"</Id>
    <Direction>Incoming</Direction>
    <PhoneNumber>+447700900001</PhoneNumber>
    <Body>Hello</Body>
    <Timestamp>2026-08-17T13:00:00+01:00</Timestamp>
    <Status>Received</Status>
    <IsRead>False</IsRead>
  </Message>
  <Message>
    <Id>" + Guid.NewGuid() + @"</Id>
    <Direction>Outgoing</Direction>
    <PhoneNumber>+447700900002</PhoneNumber>
    <Body>Hi there</Body>
    <Timestamp>2026-08-17T14:00:00+01:00</Timestamp>
    <Status>Sent</Status>
    <IsRead>True</IsRead>
  </Message>
</Messages>");

        var messages = XmlMessageArchiveService.ParseMessages(doc);
        Assert.Equal(2, messages.Count);
    }
}
