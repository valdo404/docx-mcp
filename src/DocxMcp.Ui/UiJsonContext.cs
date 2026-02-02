using System.Text.Json.Serialization;
using DocxMcp.Ui.Models;

namespace DocxMcp.Ui;

[JsonSerializable(typeof(SessionListItem[]))]
[JsonSerializable(typeof(SessionDetailDto))]
[JsonSerializable(typeof(HistoryEntryDto[]))]
[JsonSerializable(typeof(SessionEvent))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class UiJsonContext : JsonSerializerContext { }
