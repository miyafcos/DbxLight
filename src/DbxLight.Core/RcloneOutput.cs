using System.Text.Json;

namespace DbxLight.Core;

/// <summary>Never attach provider output to an exception, even through an inner exception.</summary>
internal static class RcloneOutput
{
    internal static JsonElement Json(string text, string operation)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new InvalidDataException("rclone " + operation + " returned invalid structured output.");
        }
    }
}
