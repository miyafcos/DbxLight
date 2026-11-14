using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6bTests
{
    private static void StatusPermission()
    {
        using var f = new Unit5Fixture("u6b-status-permission", writable: false);
        void AssertStatus(bool? expected, string source)
        {
            var result = f.Cli("status", "--json"); TestRunner.Equal(0, result.Exit);
            using var json = JsonDocument.Parse(result.Output);
            JsonElement value = json.RootElement.GetProperty("readOnly");
            TestRunner.Equal(expected, value.ValueKind == JsonValueKind.Null ? null : value.GetBoolean());
            TestRunner.Equal(source, json.RootElement.GetProperty("readOnlySource").GetString());
        }
        AssertStatus(true, "configured");
        f.Start(); AssertStatus(true, "active_arguments");
        RuntimeState active = f.State.Load()!;
        TestRunner.Equal(true, active.StartedReadOnly);
        f.Store.Save(f.Config with { WriteAllowedRemotes = [f.Config.Remote] });
        AssertStatus(true, "active_arguments");
        RuntimeSnapshots.Save(f.State, active with { StartedReadOnly = null });
        AssertStatus(null, "unknown");
        RuntimeSnapshots.Save(f.State, active);
        f.Controller.Stop(); AssertStatus(false, "configured");
        f.Start(); AssertStatus(false, "active_arguments");
        TestRunner.Equal(false, f.State.Load()!.StartedReadOnly);
        Console.WriteLine("A3 active read-only stays true across external writable config; stopped configured=false; restarted argument=false; legacy missing argument=null/unknown.");
    }
}
