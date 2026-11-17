using System.Net;
using System.Reflection;
using System.Text;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit8Tests
{
    private sealed class AsyncUpdateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token);
    }
    private static void NetworkReviewCases(Action<string, string, Action> test)
    {
        test("R10", "API uses JSON; ZIP and checksum keep octet-stream on redirects", AssetHeaders);
        test("R10", "ZIP budget grows with API size and covers headers and body", DownloadBudgets);
    }
    private static void AssetHeaders()
    {
        string root = Root(); var store = new UpdateStore(Path.Combine(root, "state.json")); byte[] zip = Package("0.1.1");
        string name = "DbxLight-0.1.1-win-x64.zip";
        byte[] checksum = Encoding.UTF8.GetBytes(UpdatePackage.Hash(zip) + "  " + name + "\n");
        int requests = 0;
        using var client = new UpdateClient(store, Feed, new Handler(request =>
        {
            requests++; bool api = request.RequestUri!.AbsolutePath.Contains("latest", StringComparison.Ordinal);
            TestRunner.Equal(api ? "application/vnd.github+json" : "application/octet-stream", request.Headers.Accept.Single().MediaType);
            TestRunner.Assert(request.Headers.Authorization is null);
            if (request.RequestUri.Query.Length == 0)
            {
                var redirect = Response(HttpStatusCode.Redirect); redirect.Headers.Location = new(request.RequestUri.AbsoluteUri + "?redirected=1"); return redirect;
            }
            return Response(HttpStatusCode.OK, api ? Release(zip) : request.RequestUri.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal) ? checksum : zip);
        }), "0.1.0");
        var staged = client.DownloadAsync(root).GetAwaiter().GetResult();
        TestRunner.Equal("0.1.1", UpdatePackage.ValidateDirectory(staged.Staging).Version); TestRunner.Equal(6, requests);
    }
    private sealed class WaitingStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
    }
    private static void DownloadBudgets()
    {
        var budgetMethod = typeof(UpdateClient).GetMethod("DownloadTimeout", BindingFlags.Static | BindingFlags.Public);
        TestRunner.Assert(budgetMethod is not null, "Download timeout must reflect the API asset size.");
        TestRunner.Equal(TimeSpan.FromSeconds(40), (TimeSpan)budgetMethod!.Invoke(null, [1024L * 1024])!);
        TestRunner.Equal(TimeSpan.FromSeconds(530), (TimeSpan)budgetMethod.Invoke(null, [UpdateClient.MaximumZipBytes])!);
        foreach (bool body in new[] { false, true })
        {
            byte[] zip = Package("0.1.1"); string name = "DbxLight-0.1.1-win-x64.zip";
            byte[] checksum = Encoding.UTF8.GetBytes(UpdatePackage.Hash(zip) + "  " + name + "\n");
            var budgets = new List<TimeSpan>(); bool reached = false;
            using var client = new UpdateClient(new UpdateStore(Path.Combine(Root(), "state.json")), Feed, new AsyncUpdateHandler(async (request, token) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal)) return Response(HttpStatusCode.OK, Release(zip));
                if (request.RequestUri.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)) return Response(HttpStatusCode.OK, checksum);
                reached = true;
                if (!body) { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Response(HttpStatusCode.OK); }
                return new(HttpStatusCode.OK) { Content = new StreamContent(new WaitingStream()) };
            }), "0.1.0");
            var seam = typeof(UpdateClient).GetProperty("TimeoutOverride", Hidden);
            TestRunner.Assert(seam is not null);
            seam!.SetValue(client, (Func<TimeSpan, TimeSpan>)(budget => { budgets.Add(budget); return budgets.Count == 3 ? TimeSpan.FromMilliseconds(25) : TimeSpan.FromSeconds(30); }));
            Fails<OperationCanceledException>(() => client.DownloadAsync(Root()).GetAwaiter().GetResult(), "canceled");
            TestRunner.Assert(reached); TestRunner.Equal(3, budgets.Count);
            TestRunner.Equal(TimeSpan.FromSeconds(30), budgets[0]); TestRunner.Equal(TimeSpan.FromSeconds(30), budgets[1]);
            TestRunner.Equal((TimeSpan)budgetMethod.Invoke(null, [(long)zip.Length])!, budgets[2]);
        }
    }
}
