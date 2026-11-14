using System.Text.Json;

namespace DbxLight.Core;

public sealed partial class MountController
{
    private void EnsureConnectionStorage(DbxConfig config, RuntimeState? state, bool running)
    {
        try
        {
            foreach (DbxConfig candidate in new[] { config }.Concat(state is null ? [] : new[] { state.ActiveConfig })
                .DistinctBy(c => RescueStore.Owner(c) + "|" + RescuePaths.Full(c.RescueDir)))
            {
                RescuePaths.Ancestors(candidate.CacheDir);
                string ownerPath = Path.Combine(candidate.CacheDir, "dbxl-owner.json");
                RescuePaths.Ancestors(ownerPath);
                if (File.Exists(ownerPath))
                {
                    CacheOwner owner = JsonFiles.Read<CacheOwner>(ownerPath);
                    if (owner.IdentityVersion is not (0 or 1) || owner.Pid < 0 || !Path.IsPathFullyQualified(owner.StatePath) ||
                        owner.Pid > 0 && (owner.ProcessStartedUtc == default || !Path.IsPathFullyQualified(owner.ExecutablePath) ||
                            !owner.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                        throw new PendingUploadsException("\u30ad\u30e3\u30c3\u30b7\u30e5\u306e\u6240\u6709\u3092\u78ba\u304b\u3081\u3089\u308c\u307e\u305b\u3093\u3002");
                    bool current = running && state is not null && owner.IdentityVersion == 1 && !owner.LaunchPending &&
                        owner.Pid == state.RclonePid && owner.ProcessStartedUtc == state.ProcessStartedUtc &&
                        owner.ExecutablePath.Equals(state.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
                        owner.StatePath.Equals(State.PathName, StringComparison.OrdinalIgnoreCase);
                    if (!current) EnsureCacheOwnerStopped(candidate, state ?? new RuntimeState { ActiveConfig = candidate });
                }
                try
                {
                    if ((File.GetAttributes(candidate.CacheDir) & FileAttributes.Directory) == 0) throw new IOException();
                }
                catch (DirectoryNotFoundException) { continue; }
                catch (FileNotFoundException) { continue; }
                bool live = running && state is not null && RescuePaths.Same(candidate.CacheDir, state.ActiveConfig.CacheDir);
                CacheInspection cache = live ? PendingCacheInspector.Observe(candidate.CacheDir) : PendingCacheInspector.InspectStopped(candidate.CacheDir);
                if (!cache.Clean) throw new PendingUploadsException("回復がまだです。" + cache.Detail);
            }
        }
        catch (PendingUploadsException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new PendingUploadsException("\u30ad\u30e3\u30c3\u30b7\u30e5\u3068\u6240\u6709\u306e\u8a18\u9332\u3092\u78ba\u304b\u3081\u3089\u308c\u306a\u3044\u305f\u3081\u3001\u63a5\u7d9a\u5148\u3068\u6a29\u9650\u306f\u5909\u66f4\u3067\u304d\u307e\u305b\u3093\u3002");
        }
    }
}
