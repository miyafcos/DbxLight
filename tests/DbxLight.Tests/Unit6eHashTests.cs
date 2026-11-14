using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6eTests
{
    private static void DropboxHashNames()
    {
        using var fsInfo = JsonDocument.Parse("{\"Name\":\"dropbox\",\"Root\":\"\",\"Precision\":1000000000,\"Hashes\":[\"dropbox\"]}");
        RemoteInfo info = RecoveryRemoteProbe.DecodeInfo(fsInfo.RootElement);
        TestRunner.Equal("dropbox", info.Hashes.Single());
        string saved = "3,2026-01-01 00:00:00 +0000 UTC,aaa";
        JsonElement Item(string type, string? hash, long size = 3, string time = "2026-01-01T00:00:00Z") =>
            JsonSerializer.SerializeToElement(new { IsDir = false, Size = size, ModTime = time,
                Hashes = hash is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [type] = hash } });
        foreach (string advertised in new[] { "dropbox", "DropboxHash" })
        foreach (string returned in new[] { "dropbox", "DropboxHash" })
        {
            RemoteInfo typed = info with { Hashes = [RecoveryVerification.ResolveHashName(advertised)!] };
            TestRunner.Equal("dropbox", typed.Hashes[0]);
            TestRunner.Equal(RecoveryMatch.Same, RecoveryVerification.CompareFingerprint(saved, typed, Item(returned, "AAA")));
            TestRunner.Equal(RecoveryMatch.Different, RecoveryVerification.CompareFingerprint(saved, typed, Item(returned, "bbb")));
            TestRunner.Equal(RecoveryMatch.Same, RecoveryVerification.CompareFingerprint(saved, typed, Item(returned, null)));
            TestRunner.Equal(RecoveryMatch.Same, RecoveryVerification.CompareFingerprint(saved, typed, Item(returned, "")));
            TestRunner.Equal(RecoveryMatch.Different, RecoveryVerification.CompareFingerprint(saved, typed, Item(returned, null, size: 4)));
            TestRunner.Equal(RecoveryMatch.Different, RecoveryVerification.CompareFingerprint(saved, typed, Item(returned, null, time: "2026-01-01T00:00:01Z")));
            TestRunner.Equal("aaa", RecoveryVerification.RemoteHash(Item(returned, "aaa"), advertised));
        }
        TestRunner.Equal(RecoveryMatch.Unknown, RecoveryVerification.CompareFingerprint("-1", info, Item("dropbox", null)));
        TestRunner.Equal("dropbox", RecoveryVerification.ResolveHashName("DropboxHash"));
        TestRunner.Equal("sha1", RecoveryVerification.ResolveHashName("SHA-1"));
        Console.WriteLine("E3 actual Hashes=[dropbox] and {dropbox:...}: Same/Different; unavailable hash uses common size/time; no local hash implementation.");
    }
}
