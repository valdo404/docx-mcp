namespace DocxMcp.Ui.Models;

public sealed class SessionEvent
{
    public string Type { get; set; } = "";
    public string? SessionId { get; set; }
    public int? Position { get; set; }
    public DateTime Timestamp { get; set; }
}
