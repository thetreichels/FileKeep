using System.Text.Json.Serialization;

namespace UsenetBackup.Gui;

/// <summary>
/// GUI settings persisted to gui-settings.json.
/// </summary>
public sealed record GuiSettings([property: JsonPropertyName("trayIcon")] bool TrayIcon);

/// <summary>
/// Source-generated JSON metadata for trim-safe publishing.
/// </summary>
[JsonSerializable(typeof(GuiSettings))]
internal partial class GuiJsonContext : JsonSerializerContext
{
}
