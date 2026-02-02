namespace DocxMcp.Ui.Models;

public sealed class SessionListItem
{
    public string Id { get; set; } = "";
    public string? SourcePath { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastModifiedAt { get; set; }
    public int WalCount { get; set; }
    public int CursorPosition { get; set; }
}
