using System.Xml.Linq;
using WinSMS.Helpers;
using WinSMS.Models;
using WinSMS.Services;
using Xunit;

namespace WinSMS.Tests;

public class SmsParsingTests
{
    [Fact]
    public void ParseCmglResponse_ParsesSingleMessage()
    {
        var response = "+CMGL: 1,\"REC READ\",\"+447700900001\",,\"26/08/17,13:00:00+04\"\r\nHello World\r\nOK\r\n";
        var messages = SmsService.ParseCmglResponse(response);
        Assert.Single(messages);
        Assert.Equal("+447700900001", messages[0].PhoneNumber);
        Assert.Equal("Hello World", messages[0].Body);
        Assert.Equal(1, messages[0].ModemMessageIndex);
        Assert.True(messages[0].IsRead);
        Assert.Equal(SmsDirection.Incoming, messages[0].Direction);
    }

    [Fact]
    public void ParseCmglResponse_ParsesUnreadMessage()
    {
        var response = "+CMGL: 2,\"REC UNREAD\",\"+447700900002\",,\"26/08/17,14:00:00+04\"\r\nTest body\r\nOK\r\n";
        var messages = SmsService.ParseCmglResponse(response);
        Assert.Single(messages);
        Assert.False(messages[0].IsRead);
        Assert.Equal(SmsDirection.Incoming, messages[0].Direction);
    }

    [Fact]
    public void ParseCmglResponse_ParsesOutgoingMessage()
    {
        var response = "+CMGL: 3,\"STO SENT\",\"+447700900003\",,\"26/08/17,15:00:00+04\"\r\nSent body\r\nOK\r\n";
        var messages = SmsService.ParseCmglResponse(response);
        Assert.Single(messages);
        Assert.Equal(SmsDirection.Outgoing, messages[0].Direction);
        Assert.Equal(SmsStatus.Sent, messages[0].Status);
    }

    [Fact]
    public void ParseCmglResponse_ParsesMultipleMessages()
    {
        var response =
            "+CMGL: 1,\"REC READ\",\"+447700900001\",,\"26/08/17,13:00:00+04\"\r\nFirst\r\n" +
            "+CMGL: 2,\"REC UNREAD\",\"+447700900002\",,\"26/08/17,14:00:00+04\"\r\nSecond\r\nOK\r\n";
        var messages = SmsService.ParseCmglResponse(response);
        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public void ParseCmglResponse_EmptyResponse_ReturnsEmptyList()
    {
        var messages = SmsService.ParseCmglResponse("OK\r\n");
        Assert.Empty(messages);
    }

    [Fact]
    public void ParseCmgrResponse_ParsesMessage()
    {
        var response = "+CMGR: \"REC READ\",\"+447700900001\",,\"26/08/17,13:00:00+04\"\r\nHello\r\nOK\r\n";
        var message = SmsService.ParseCmgrResponse(response, 5);
        Assert.NotNull(message);
        Assert.Equal("+447700900001", message.PhoneNumber);
        Assert.Equal("Hello", message.Body);
        Assert.Equal(5, message.ModemMessageIndex);
    }

    [Fact]
    public void ParseCmgrResponse_InvalidResponse_ReturnsNull()
    {
        var message = SmsService.ParseCmgrResponse("ERROR\r\n", 1);
        Assert.Null(message);
    }

    [Fact]
    public void ParseTimestamp_ValidGsmTimestamp_ReturnsCorrectDate()
    {
        var ts = SmsService.ParseTimestamp("26/08/17,13:00:00+04");
        Assert.Equal(2026, ts.Year);
        Assert.Equal(8, ts.Month);
        Assert.Equal(17, ts.Day);
        Assert.Equal(13, ts.Hour);
        Assert.Equal(TimeSpan.FromMinutes(60), ts.Offset); // +04 quarters = 60 minutes
    }

    [Fact]
    public void ParseTimestamp_EmptyString_ReturnsNow()
    {
        var before = DateTimeOffset.Now.AddSeconds(-1);
        var ts = SmsService.ParseTimestamp(string.Empty);
        Assert.True(ts >= before);
    }

    [Theory]
    [InlineData("+CME ERROR: 10", "CME ERROR: 10")]
    [InlineData("+CMS ERROR: 330", "CMS ERROR: 330")]
    [InlineData("ERROR", "ERROR")]
    public void ExtractError_ReturnsCorrectMessage(string response, string expected)
    {
        var error = SmsService.ExtractError(response);
        Assert.Contains(expected, error!);
    }

    [Fact]
    public void ExtractError_OkResponse_ReturnsNull()
    {
        var error = SmsService.ExtractError("OK\r\n");
        Assert.Null(error);
    }
}

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
            new XElement("Body", original.Body),
            new XElement("Timestamp", original.Timestamp.ToString("O")),
            new XElement("Status", original.Status.ToString()),
            new XElement("IsRead", original.IsRead.ToString()),
            new XElement("ModemMessageIndex", original.ModemMessageIndex.Value));

        var parsed = XmlMessageArchiveService.ParseMessageElement(xml);

        Assert.NotNull(parsed);
        Assert.Equal(original.Id, parsed.Id);
        Assert.Equal(original.PhoneNumber, parsed.PhoneNumber);
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
