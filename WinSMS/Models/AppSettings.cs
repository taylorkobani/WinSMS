namespace WinSMS.Models;

public class AppSettings
{
    public string SelectedPort { get; set; } = string.Empty;
    public int BaudRate { get; set; } = 115200;
    public int CommandTimeoutMs { get; set; } = 5000;
    public bool AutoConnect { get; set; } = false;
    public int PollingIntervalSeconds { get; set; } = 30;
    public string ArchiveDirectory { get; set; } = "Data/Messages";
}
