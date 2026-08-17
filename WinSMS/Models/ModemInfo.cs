namespace WinSMS.Models;

public class ModemInfo
{
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public string? FirmwareVersion { get; set; }
    public string PortName { get; set; } = string.Empty;
    public int BaudRate { get; set; }
}
