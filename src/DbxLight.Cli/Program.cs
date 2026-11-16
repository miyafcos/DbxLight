using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Cli;

public static class Program
{
    public static int Main(string[] args) => MainCore(args, null);

    internal static int MainCore(string[] args, MountController? controller)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string requestedCommand = CommandName(args);
        bool json = args.Contains("--json", StringComparer.Ordinal) ||
            (requestedCommand == "agent-guide" && Array.IndexOf(args, "--format") is int formatIndex &&
             formatIndex >= 0 && formatIndex + 1 < args.Length && args[formatIndex + 1] == "json");
        bool agent = AgentCommands.IsCommand(requestedCommand);
        bool reading = false;
        try
        {
            var words = args.ToList();
            if (words.Contains("--json"))
            {
                words.Remove("--json");
                if (words.Contains("--json")) throw new ArgumentException("Duplicate option: --json");
            }
            string? configPath = TakeOption(words, "--config");
            string? statePath = TakeOption(words, "--state");
            if (words.Count == 0 || words[0] is "help" or "--help" or "-h")
            {
                if (json) WriteJson(new { ok = true, usage = HelpText }); else Help();
                return 0;
            }
            controller ??= new MountController(new ConfigStore(configPath), new StateStore(statePath));
            string command = words[0];
            string[] operands = words.Skip(1).ToArray();
            reading = agent || command is "status" or "hidden" or "folders" or "doctor" ||
                command == "config" && operands.FirstOrDefault() == "show" || command == "rescue" && operands.FirstOrDefault() == "list";
            if (AgentCommands.IsCommand(command)) return AgentCommands.Run(controller, command, operands, json);
            void Done(object data, Action text)
            {
                if (json) WriteJson(new { ok = true, result = data }); else text();
            }
            switch (command)
            {
                case "start":
                    Arity(operands, 0);
                    StartResult started = controller.Start();
                    Done(started, () => PrintStart(started));
                    return 0;
                case "run":
                    Arity(operands, 0);
                    using (var cancellation = new CancellationTokenSource())
                    {
                        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
                        Console.CancelKeyPress += handler;
                        try
                        {
                            return RunResident(controller, json, cancellation.Token);
                        }
                        finally { Console.CancelKeyPress -= handler; }
                    }
                case "stop":
                    if (operands.Length > 1 || (operands.Length == 1 && operands[0] != "--force")) throw new ArgumentException("Usage: stop [--force]");
                    StopResult stopped = controller.Stop(operands.Length == 1);
                    Done(stopped, () => Console.WriteLine(stopped.Message ?? (stopped.WasRunning ? $"Stopped rclone pid={stopped.Pid}." : "Not running.")));
                    return 0;
                case "rescue":
                    if (operands.Length == 2 && operands[0] == "acknowledge")
                    {
                        controller.AcknowledgeRescue(operands[1]);
                        Done(new { acknowledged = true }, () => Console.WriteLine("確認済みにしました。原本は残っています。"));
                        return 0;
                    }
                    if (operands.Length < 1 || operands[0] != "list") throw new ArgumentException("Usage: rescue list [--all | --max N] [--json] | rescue acknowledge <id>");
                    var rescueOptions = new CommandOptions(operands.Skip(1).ToArray());
                    bool allRescues = rescueOptions.Flag("--all");
                    int? rescueMaximum = rescueOptions.Number("--max");
                    if (rescueOptions.Rest().Length > 0 || allRescues && rescueMaximum is not null || rescueMaximum is < 1 or > 10000)
                        throw new ArgumentException("Use --all or --max 1..10000.");
                    int rescueLimit = rescueMaximum ?? 50;
                    var rescuePage = ReadControl(() => controller.RescueList(null)).Take(allRescues ? int.MaxValue : rescueLimit + 1).ToArray();
                    bool rescueTruncated = !allRescues && rescuePage.Length > rescueLimit;
                    var rescued = allRescues ? rescuePage : rescuePage.Take(rescueLimit).ToArray();
                    Done(new { items = rescued, truncated = rescueTruncated, maximum = allRescues ? (int?)null : rescueLimit }, () =>
                    {
                        foreach (var item in rescued)
                            Console.WriteLine((item.RecordStatus == "unknown" ? item.RecordPath : item.CloudPath ?? item.OriginalPath) + "\t" + item.Reason + "\t" + item.DestinationPath + "\t" + item.NextAction);
                        if (rescueTruncated) Console.WriteLine("続きがあります。dbxl rescue list --all で全件を確認してください。");
                    });
                    return 0;
                case "recovery":
                    if (operands.Length is < 1 or > 2 || operands[0] != "clear" ||
                        (operands.Length == 2 && operands[1] != "--i-understand-pending-may-be-lost"))
                        throw new ArgumentException("Usage: recovery clear --i-understand-pending-may-be-lost");
                    controller.ClearRecovery(operands.Length == 2);
                    Done(new { cleared = true }, () => Console.WriteLine("Recovery explicitly cleared; pending data may remain in the old cache."));
                    return 0;
                case "restart":
                    Arity(operands, 0);
                    StartResult restarted = controller.Restart();
                    Done(restarted, () => PrintStart(restarted));
                    return 0;
                case "status":
                    Arity(operands, 0);
                    var observation = controller.ReadRuntime();
                    DbxStatus status = observation.Status;
                    DbxConfig? statusConfig = status.Preparation is not null ? null : (status.Running ? observation.Runtime?.ActiveConfig : null) ?? ReadControl(controller.Config.Load);
                    bool configured = status.Preparation is not null || !string.IsNullOrWhiteSpace(statusConfig?.Remote);
                    if (json)
                    {
                        var fields = JsonSerializer.SerializeToElement(status, JsonFiles.Options).EnumerateObject()
                            .ToDictionary(p => p.Name, p => (object?)p.Value);
                        fields["ok"] = configured && status.Preparation is null && !status.RecoveryRequired && !status.ProcessUncertain && status.StatusError is null && (!status.Running || status.Ready);
                        if (!(bool)fields["ok"]!)
                        {
                            fields["code"] = status.Preparation is not null ? "preparing" : !configured ? "not_configured" : status.ProcessUncertain ? "process_uncertain" : status.RecoveryRequired ? "recovery_required" : "status_unknown";
                            fields["message"] = status.Preparation is { } preparing ? "起動の準備中: " + preparing.Stage : !configured ? "Remote is not configured. Check --config or DBXL_CONFIG." : RecoveryMessage(status, status.RecoveryLocation ?? status.StatusError ?? "Mount is not ready.");
                        }
                        WriteJson(fields);
                    }
                    else
                    {
                        Console.WriteLine($"running={status.Running} ready={status.Ready} pid={status.RclonePid} queued={status.UploadsQueued} uploading={status.UploadsInProgress} errors={status.ErroredFiles}" + (status.StatusError is null ? "" : " error=" + status.StatusError));
                        if (status.Preparation is { } preparing)
                            Console.WriteLine($"起動の準備中: {preparing.Stage} {preparing.Completed}/{preparing.Total}; 開始 {preparing.StartedUtc:O}" + (preparing.Stopping ? " 退避の区切りで止めます。" : ""));
                        if (status.RecoveryRequired && status.Running && !status.ProcessUncertain) Console.WriteLine(ConfirmingMessage);
                        if (status.RescueCountsKnown) PrintRescue(status.RescueNotice);
                        else Console.WriteLine("退避したファイルの件数は準備中のため未確認です。");
                        Console.WriteLine(status.LogCapture.Reason + (status.LogCapture.Directory is null ? "" : " ログのフォルダ: " + status.LogCapture.Directory));
                    }
                    return status.Running && !status.Ready ? 1 : 0;
                case "hide":
                    Arity(operands, 1);
                    DbxConfig hidden = controller.Hide(operands[0]);
                    Done(new { hiddenPaths = hidden.HiddenPaths }, () => PrintHidden(hidden));
                    return 0;
                case "unhide":
                    Arity(operands, 1);
                    DbxConfig shown = controller.Unhide(operands[0]);
                    Done(new { hiddenPaths = shown.HiddenPaths }, () => PrintHidden(shown));
                    return 0;
                case "hidden":
                    var hiddenPaging = Paging(operands); Arity(hiddenPaging.Words, 0);
                    DbxConfig current = ReadControl(controller.Config.Load);
                    string[] hiddenPage = current.HiddenPaths.Skip(hiddenPaging.Offset).Take(hiddenPaging.Max).ToArray();
                    bool hiddenTruncated = current.HiddenPaths.Length > hiddenPaging.Offset + hiddenPage.Length;
                    var hiddenNext = Next(hiddenPaging.Offset + hiddenPage.Length, hiddenTruncated);
                    Done(new { hiddenPaths = hiddenPage, truncated = hiddenTruncated, continuation = hiddenNext }, () =>
                    {
                        foreach (string item in hiddenPage) Console.WriteLine(item);
                        if (hiddenTruncated) Console.Error.WriteLine(hiddenNext!.Hint);
                    });
                    return 0;
                case "folders":
                    var folderPaging = Paging(operands);
                    if (folderPaging.Words.Length > 1) throw new ArgumentException("Usage: folders [path] [--max N] [--offset N]");
                    var folders = FolderBrowser.List(ReadControl(controller.Config.Load), folderPaging.Words.FirstOrDefault() ?? "");
                    var folderPage = folders.Skip(folderPaging.Offset).Take(folderPaging.Max).ToArray();
                    bool foldersTruncated = folders.Count > folderPaging.Offset + folderPage.Length;
                    var foldersNext = Next(folderPaging.Offset + folderPage.Length, foldersTruncated);
                    Done(new { folders = folderPage, truncated = foldersTruncated, continuation = foldersNext }, () =>
                    {
                        foreach (RemoteFolder folder in folderPage) Console.WriteLine((folder.Hidden ? "[hidden] " : "[shown]  ") + folder.Path);
                        if (foldersTruncated) Console.Error.WriteLine(foldersNext!.Hint);
                    });
                    return 0;
                case "set":
                    Arity(operands, 2);
                    controller.UpdateConfig(config => ConfigStore.Set(config, operands[0], operands[1]));
                    Done(new { saved = operands[0] }, () => Console.WriteLine("Saved " + operands[0] + "."));
                    return 0;
                case "config":
                    if (operands.Length != 1 || operands[0] != "show") throw new ArgumentException("Usage: config show");
                    DbxConfig config = ReadControl(controller.Config.Load);
                    Done(config, () => WriteJson(config));
                    return 0;
                case "doctor":
                    Arity(operands, 0);
                    var checks = Doctor.Check(ReadControl(controller.Config.Load));
                    if (json) WriteJson(new { ok = checks.All(check => check.Passed), code = checks.All(check => check.Passed) ? "checked" : "check_failed", message = "Environment checks completed.", checks });
                    else foreach (var check in checks) Console.WriteLine($"{(check.Passed ? "PASS" : "FAIL")} {check.Name}: {check.Detail}");
                    return checks.All(check => check.Passed) ? 0 : 1;
                default: throw new ArgumentException("Unknown command: " + command);
            }
        }
        catch (ControlBusyException) when (reading)
        {
            if (json)
            {
                var fields = requestedCommand == "status"
                    ? JsonSerializer.SerializeToElement(new DbxStatus(), JsonFiles.Options).EnumerateObject()
                        .ToDictionary(p => p.Name, _ => (object?)null)
                    : new Dictionary<string, object?>();
                fields["ok"] = false; fields["code"] = "busy"; fields["message"] = BusyMessage;
                WriteJson(fields);
            }
            else Console.Error.WriteLine(BusyMessage);
            return requestedCommand == "status" ? 0 : 3;
        }
        catch (AgentCommandException e)
        {
            string message = requestedCommand == "stat" && e.Code == "recovery_required" ? RecoveryMessage(e.Status, e.Message) : e.Message;
            if (json) WriteJson(new { ok = false, code = e.Code, message, status = e.Status, skippedUnsafe = e.SkippedUnsafe });
            else Console.Error.WriteLine(message);
            return e.ExitCode;
        }
        catch (PendingUploadsException e)
        {
            var failure = StartupFailure.Get(e);
            if (json) WriteJson(new { ok = false, code = "pending", message = failure?.Message ?? e.Message,
                failureClass = failure?.Classification, location = failure?.Location, nextAction = failure?.NextAction });
            else Console.Error.WriteLine(failure is null ? e.Message : StartupFailure.PublicMessage(e));
            return 3;
        }
        catch (Exception e) when (e is ArgumentException or IOException or InvalidOperationException or TimeoutException or JsonException or HttpRequestException or System.ComponentModel.Win32Exception or System.Net.Sockets.SocketException or UnauthorizedAccessException or FormatException or OverflowException or OperationCanceledException or KeyNotFoundException or NotSupportedException)
        {
            int exit = agent ? e is ArgumentException or FormatException or OverflowException ? 2 : 4 : 1;
            var failure = StartupFailure.Get(e);
            if (json) WriteJson(new { ok = false, code = e is ArgumentException or FormatException or OverflowException ? "usage" : "failed",
                message = failure?.Message ?? (requestedCommand is "start" or "restart" or "run" ? StartupFailure.PublicMessage(e) : e.Message), failureClass = failure?.Classification, logDirectory = failure?.LogDirectory, location = failure?.Location, nextAction = failure?.NextAction });
            else Console.Error.WriteLine(failure is not null || requestedCommand is "start" or "restart" or "run" ? StartupFailure.PublicMessage(e) : e.Message);
            return exit;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (json) WriteJson(new { ok = false, code = "failed", message = StartupFailure.PublicMessage(e) });
            else Console.Error.WriteLine(StartupFailure.PublicMessage(e));
            return agent ? 4 : 1;
        }
    }

    internal const string ConfirmingMessage = "Uploads from before the last stop are still being confirmed. This clears by itself when they finish; retry in a few seconds. If it persists, tell a person.";
    internal static string RecoveryMessage(DbxStatus? status, string fallback) =>
        status is { RecoveryRequired: true, Running: true, ProcessUncertain: false } ? ConfirmingMessage : fallback;

    internal const string BusyMessage = "Another DbxLight operation is running. Retry in a few seconds.";
    private static T ReadControl<T>(Func<T> read)
    {
        if (ControlGate.TryExecute(read, TimeSpan.FromSeconds(2), out var result)) return result;
        throw new ControlBusyException();
    }

    private static string CommandName(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--config" or "--state") { i++; continue; }
            if (args[i] == "--json") continue;
            return args[i];
        }
        return "";
    }
    private static (int Max, int Offset, string[] Words) Paging(string[] operands)
    {
        var options = new CommandOptions(operands);
        int max = options.Number("--max") ?? 1000, offset = options.Number("--offset") ?? 0;
        if (max is < 1 or > 10000 || offset is < 0 or > 1000000) throw new ArgumentException("max=1..10000; offset=0..1000000.");
        return (max, offset, options.Rest());
    }
    private static AgentContinuation? Next(int offset, bool truncated) => truncated
        ? AgentContinuation.ForOffset(offset) : null;

    private static string? TakeOption(List<string> words, string option)
    {
        int index = words.IndexOf(option);
        if (index < 0) return null;
        if (index + 1 >= words.Count || words[index + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException(option + " requires a path.");
        string value = words[index + 1];
        words.RemoveRange(index, 2);
        if (words.Contains(option)) throw new ArgumentException("Duplicate option: " + option);
        return value;
    }

    private static void Arity(string[] words, int expected)
    {
        if (words.Length != expected) throw new ArgumentException($"Expected {expected} argument(s).");
    }
    private static void PrintStart(StartResult result)
    {
        Console.WriteLine($"{(result.AlreadyRunning ? "Already running" : "Started")} rclone pid={result.Pid}.");
        PrintRescue(result.RescueNotice);
        Console.WriteLine(result.LogCapture.Reason + (result.LogCapture.Directory is null ? "" : " ログのフォルダ: " + result.LogCapture.Directory));
    }
    internal static int RunResident(MountController controller, bool json, CancellationToken token)
    {
        bool preparationCancelled = false;
        int exit = controller.Run(token, started => { if (!json) PrintStart(started); },
            () => preparationCancelled = true);
        RescueNotice? notice = null;
        try
        {
            DbxStatus status = controller.Status();
            if (status.RescueCountsKnown) notice = status.RescueNotice;
        }
        catch { /* The run result must retain an explicit unknown, never a fabricated zero. */ }
        object finalNotice = notice is not null ? notice : new
        {
            unacknowledged = (int?)null, unknownRecords = (int?)null, foreignRecords = (int?)null,
            hint = "退避したファイルの件数は未確認です。dbxl rescue list --json で確認してください。"
        };
        if (json) WriteJson(new { ok = exit == 0, code = preparationCancelled ? "cancelled" : exit == 0 ? "completed" : "failed",
            message = "Resident run ended.", preparationCancelled, rescueCountsKnown = notice is not null, rescueNotice = finalNotice });
        else
        {
            if (preparationCancelled) Console.WriteLine("起動の準備を取り消して終了しました。");
            if (notice is not null) PrintRescue(notice);
            else Console.WriteLine("退避したファイルの件数は未確認です。dbxl rescue list --json で確認してください。");
        }
        return exit;
    }

    private static void PrintRescue(RescueNotice notice) =>
        Console.WriteLine($"退避したファイル（未確認）={notice.Unacknowledged} 件; 確認できない記録={notice.UnknownRecords} 件; ほかの設定={notice.ForeignRecords} 件。 " + notice.Hint);
    private static void PrintHidden(DbxConfig config)
    {
        foreach (string path in config.HiddenPaths) Console.WriteLine(path);
    }
    private static readonly JsonSerializerOptions OutputOptions = new(JsonFiles.Options)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };
    internal static void WriteJson(object value) => Console.WriteLine(JsonSerializer.Serialize(value, OutputOptions));
    private const string HelpText = "dbxl [--config <path>] [--state <path>] [--json] help|run|start|stop [--force]|restart|status|hide <path>|unhide <path>|hidden [--max N] [--offset N]|folders [path] [--max N] [--offset N]|set <key> <value>|config show|doctor|recovery clear --i-understand-pending-may-be-lost|rescue list [--all|--max N]|rescue acknowledge <id>|ls <path> [--recursive] [--depth N] [--max N] [--offset N] [--include-hidden] [--time-limit seconds]|find <term> [--in <path>] [--type file|dir] [--depth N] [--max N] [--offset N] [--time-limit seconds]|stat <path> [--time-limit seconds]|wait <path>...|wait --all [--timeout seconds]|refresh [path]|resolve <path-or-url>|agent-guide [--format markdown|json]";
    private static void Help() => Console.WriteLine(HelpText);
}
