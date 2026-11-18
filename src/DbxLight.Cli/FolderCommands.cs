using DbxLight.Core;

namespace DbxLight.Cli;

internal static class FolderCommands
{
    private const string Usage = "使い方は path <パス>、share show <パス>、share invite <パス> --to <メール> --access viewer|editor [--message <文>] [--execute --confirm <確認値>] です。";

    internal static int Run(string[] args, MountController? controller, Func<DbxConfig, DropboxFolderClient> open)
    {
        bool json = WantsJson(args);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var words = new List<string>();
            var recipients = new List<string>();
            string? access = null, message = null, configOption = null, stateOption = null, confirmation = null;
            bool execute = false, jsonOption = false;
            for (int i = 0; i < args.Length; i++)
            {
                string option = args[i];
                string Value()
                {
                    if (++i >= args.Length) throw new ArgumentException(Usage);
                    return args[i];
                }
                switch (option)
                {
                    case "--config" when configOption is null: configOption = Value(); break;
                    case "--state" when stateOption is null: stateOption = Value(); break;
                    case "--json" when !jsonOption: jsonOption = true; break;
                    case "--execute" when !execute: execute = true; break;
                    case "--confirm" when confirmation is null: confirmation = Value(); break;
                    case "--to": recipients.Add(Value()); break;
                    case "--access" when access is null: access = Value(); break;
                    case "--message" when message is null: message = Value(); break;
                    default:
                        if (option.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException(Usage);
                        words.Add(option); break;
                }
            }

            bool path = words.Count == 2 && words[0] == "path";
            bool show = words.Count == 3 && words[0] == "share" && words[1] == "show";
            bool invite = words.Count == 3 && words[0] == "share" && words[1] == "invite";
            if (!(path || show || invite) || !invite && (execute || access is not null || message is not null || recipients.Count != 0) ||
                confirmation is not null && !execute)
                throw new ArgumentException(Usage);
            if (invite && (access is null || recipients.Count == 0)) throw new ArgumentException(Usage);
            if (execute && string.IsNullOrEmpty(confirmation)) throw FolderOperationException.For("confirmation_required");
            var invitation = invite ? FolderInviteRequest.Create(recipients, access!, message) : null;

            string configPath = new ConfigStore(configOption ?? controller?.Config.PathName).PathName;
            string selected = words[^1];
            var token = cancellation.Token;
            string statePath = new StateStore(stateOption ?? controller?.State.PathName).PathName;
            var mount = new FolderShareMount(configPath, statePath, controller?.FolderMountInspector);
            if (path)
            {
                DbxConfig config = DropboxFolderClient.ReadConfiguration(configPath);
                string copied = DropboxCopyPaths.CopyAsync(config, [selected], open, token, mount).GetAwaiter().GetResult()[0];
                return Success(new { path = copied }, () => Console.WriteLine(copied));
            }

            var session = new ShareFolderSession(selected, mount, open);
            var info = session.LoadAsync(requireMount: invite, includeCopyPath: false, token, verifySelection: !execute).GetAwaiter().GetResult();
            var view = info.View;
            var account = view.Target.Account;
            var connection = new { displayName = account.DisplayName, account.TeamSpace, account.NamespaceKind,
                remote = info.Config.Remote, mountVerified = info.Mount is not null };
            if (show)
                return Success(new { path = info.LocalPath, connection, view.Status.Shared, view.Status.Inherited, view.Status.Access, view.Status.Members },
                    () => PrintStatus(info));

            FolderInvitePlan plan = FolderSharing.Plan(view, info.LocalPath, invitation!);
            string currentConfirmation = plan.Confirmation(info.Mount!);
            if (!execute)
                return Success(new { path = info.LocalPath, connection, plan.Request.To, plan.Request.Access, plan.Request.Message,
                    plan.WouldShareFolder, plan.Inherited, confirmation = currentConfirmation, executed = false }, () =>
                {
                    Console.WriteLine("招待の確認（送信前）");
                    Console.WriteLine(info.LocalPath);
                    Console.WriteLine("接続先 " + account.ConnectionLabel);
                    Console.WriteLine(plan.WouldShareFolder ? "共有フォルダにします。" : "共有済みのフォルダです。");
                    if (plan.Inherited) Console.WriteLine(FolderSharing.InheritedNotice);
                    Console.WriteLine(FolderSharing.AccessLabel(plan.Request.Access) + "で招待する相手");
                    foreach (string recipient in plan.Request.To) Console.WriteLine(recipient);
                    if (plan.Request.Message is { } note) Console.WriteLine(note);
                    Console.WriteLine("確認値 " + currentConfirmation);
                    Console.WriteLine("内容を確認し、--execute --confirm <確認値> を付けて実行してください。");
                });

            if (!string.Equals(confirmation, currentConfirmation, StringComparison.Ordinal))
                throw FolderOperationException.For("confirmation_changed");
            var (result, error) = session.InviteAsync(info, plan, token).GetAwaiter().GetResult();
            if (error is not null) return Failure(error.Code, result.Progress + " " + error.Message, error.ExitCode, result);
            return Success(result, () => Console.WriteLine(result.Progress));
        }
        catch (FolderOperationException e) { return Failure(e.Code, e.Message, e.ExitCode); }
        catch (ArgumentException e) { return Failure("usage", e.Message, 2); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            var error = FolderOperationException.For("remote_error");
            return Failure(error.Code, error.Message, error.ExitCode);
        }
        finally { Console.CancelKeyPress -= cancel; }

        int Success(object result, Action text)
        {
            if (json) Program.WriteJson(new { ok = true, result }); else text();
            return 0;
        }

        int Failure(string code, string explanation, int exitCode, FolderInviteResult? result = null)
        {
            if (!json) Console.Error.WriteLine(explanation);
            else if (result is null) Program.WriteJson(new { ok = false, code, message = explanation });
            else Program.WriteJson(new { ok = false, code, message = explanation, result });
            return exitCode;
        }
    }

    private static bool WantsJson(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--config" or "--state" or "--to" or "--access" or "--message" or "--confirm") { i++; continue; }
            if (args[i] is "--json" or "--execute") return true;
        }
        return false;
    }

    private static void PrintStatus(ShareFolderInfo info)
    {
        Console.WriteLine(info.LocalPath);
        Console.WriteLine("接続先 " + info.View.Target.Account.ConnectionLabel + " " + info.Config.Remote);
        if (info.Mount is null) Console.WriteLine(FolderOperationException.For("mount_unverified").Message);
        var status = info.View.Status;
        Console.WriteLine(status.Shared ? "共有済み" : status.Inherited ? "上位フォルダから共有の権限を引き継いでいます。" : "未共有");
        foreach (var member in status.Members)
            Console.WriteLine($"{member.Name}\t{member.Email}\t{FolderSharing.AccessLabel(member.Access)}" +
                (member.Kind == "invitee" ? "\t招待済み" : ""));
    }
}
