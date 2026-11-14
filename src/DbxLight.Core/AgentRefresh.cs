using System.Text.Json;

namespace DbxLight.Core;

public sealed record AgentRefreshResult(bool Ok, string Path, string Source, DbxStatus Status);

public sealed partial class AgentMetadata
{
    public AgentRefreshResult Refresh(string path = "")
    {
        string relative = Relative(path); EnsureVisible(relative);
        if (rc is null) throw new AgentCommandException(6, "not_running", "The mount is not running; there is no mounted listing to refresh.", Status);
        _ = ReadListingChildren(relative); // Verify the listed directory and refuse reparse-point ancestors before refresh.
        JsonElement result = (relative.Length == 0 ? rc.Call("vfs/refresh") : rc.Call("vfs/refresh", new { dir = relative })).GetProperty("result");
        if (!result.TryGetProperty(relative, out var refreshed) || refreshed.GetString() != "OK")
            throw new AgentCommandException(4, "refresh_failed", "The requested directory could not be refreshed.", Status);
        UpdateStatus();
        return new(true, relative, "vfs/refresh", Status);
    }
}
