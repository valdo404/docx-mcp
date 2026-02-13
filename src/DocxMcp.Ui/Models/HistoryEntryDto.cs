namespace DocxMcp.Ui.Models;

public sealed class HistoryEntryDto
{
    public int Position { get; set; }
    public DateTime Timestamp { get; set; }
    public string Description { get; set; } = "";
    public bool IsCheckpoint { get; set; }
    public string Patches { get; set; } = "";
}
