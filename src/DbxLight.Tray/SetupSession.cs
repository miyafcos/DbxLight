using DbxLight.Core;

namespace DbxLight.Tray;

public enum SetupStep { Requirements = 1, Login, Scope, Options, Finish }

public sealed class SetupSession : IDisposable
{
    private readonly MountController controller;
    private readonly RcloneLogin login;
    private PrivateLoginFile? fileChange;
    private CancellationTokenSource? authorizing;
    private Task<LoginResult>? authorization;
    private bool disposed;
    public DbxConfig Original { get; }
    public DbxConfig Draft { get; private set; }
    public SetupStep Step { get; private set; } = SetupStep.Requirements;
    public bool Connected { get; private set; }
    public bool Committed { get; private set; }
    public string Message { get; private set; } = "";
    public IReadOnlyList<DoctorCheck> Checks { get; private set; } = [];

    public SetupSession(MountController controller, string? loginConfigPath = null)
        : this(controller, new RcloneLogin(controller.Config.Load().RclonePath, loginConfigPath ?? CredentialSwitch.KeyPath(controller.Config.PathName))) { }

    internal SetupSession(MountController controller, RcloneLogin login)
    {
        this.controller = controller;
        this.login = login.AtPath(CredentialSwitch.KeyPath(controller.Config.PathName));
        Original = controller.Config.Load(); Draft = Original with { RclonePath = login.Executable, WriteAllowedRemotes = [] };
    }

    public void CheckRequirements() => Checks = Doctor.Check(Draft);
    public bool Next()
    {
        if (Step == SetupStep.Requirements && Checks.Where(c => c.Name is "rclone" or "WinFsp").Count(c => c.Passed) == 2)
        { Step = SetupStep.Login; return true; }
        if (Step == SetupStep.Scope && ScopeVerified) { Step = SetupStep.Options; return true; }
        if (Step == SetupStep.Options) { Draft.Validate(forMount: true); Step = SetupStep.Finish; return true; }
        return false;
    }
    public void Back()
    {
        if (Step > SetupStep.Requirements) Step--;
    }

    public Task<IReadOnlyList<string>> ExistingRemotesAsync(CancellationToken cancellation = default) => login.ListRemotesAsync(false, cancellation);
    public async Task<bool> NeedsOverwriteAsync(CancellationToken cancellation = default) =>
        (await login.ListRemotesAsync(true, cancellation).ConfigureAwait(false)).Contains(RcloneLogin.RemoteName + ":", StringComparer.Ordinal);

    public Task<LoginResult> AuthorizeAsync(bool overwriteConfirmed, CancellationToken cancellation = default)
    {
        if (authorization is { IsCompleted: false }) throw new InvalidOperationException("ログインは実行中です。");
        authorizing?.Dispose();
        authorizing = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        return authorization = AuthorizeCoreAsync(overwriteConfirmed, authorizing.Token);
    }

    private async Task<LoginResult> AuthorizeCoreAsync(bool overwriteConfirmed, CancellationToken cancellation)
    {
        if (Step != SetupStep.Login) throw new InvalidOperationException("ログインの段を開いてください。");
        await Task.Run(() => controller.PrepareConnectionChange(Original), cancellation).ConfigureAwait(false);
        if (fileChange is null && await NeedsOverwriteAsync(cancellation).ConfigureAwait(false) && !overwriteConfirmed)
            return new(LoginOutcome.Failed, "dbxlight が既にあります。上書きの確認後にログインしてください。");
        fileChange?.Dispose(); fileChange = null;
        fileChange = login.BeginFileChange(controller.Config);
        LoginResult result = await login.AuthorizeAsync(fileChange.WorkingPath, cancellation).ConfigureAwait(false);
        Message = result.Message;
        if (result.Succeeded)
        {
            Connected = true;
            Draft = Draft with { Remote = "dbxlight:", RcloneConfigPath = login.ConfigPath, HiddenPaths = [], WriteAllowedRemotes = [], TestOverrides = null };
            ScopeVerified = false; TeamRoot = false; Folders = [];
            Step = SetupStep.Scope;
        }
        else { fileChange.Dispose(); fileChange = null; Connected = false; }
        return result;
    }

    internal DbxConfig BrowsingConfig => fileChange is null ? Draft : Draft with { RcloneConfigPath = fileChange.WorkingPath };

    public IReadOnlyList<RemoteFolder> Folders { get; private set; } = [];
    public bool TeamRoot { get; private set; }
    public bool ScopeVerified { get; private set; }

    public async Task UseExistingRemoteAsync(string name, CancellationToken cancellation = default)
    {
        if (Step != SetupStep.Login) throw new InvalidOperationException("ログインの段を開いてください。");
        IReadOnlyList<string> names = await ExistingRemotesAsync(cancellation).ConfigureAwait(false);
        if (!names.Contains(name, StringComparer.Ordinal)) throw new ArgumentException("一覧から接続先を選んでください。");
        await Task.Run(() => controller.PrepareConnectionChange(Original), cancellation).ConfigureAwait(false);
        fileChange?.Dispose(); fileChange = null;
        Draft = Draft with { Remote = name, RcloneConfigPath = null, HiddenPaths = [], WriteAllowedRemotes = [], TestOverrides = null };
        Connected = true; ScopeVerified = false; TeamRoot = false; Folders = [];
        Message = "選んだリモートのフォルダを確認してください。";
        Step = SetupStep.Scope;
    }

    public async Task SelectScopeAsync(bool teamRoot, CancellationToken cancellation = default)
    {
        if (Step != SetupStep.Scope || !Connected) throw new InvalidOperationException("見える範囲の段を開いてください。");
        ScopeVerified = false; Folders = [];
        string own = Draft.Remote.TrimEnd('/');
        Draft = Draft with { Remote = own + (teamRoot ? "/" : "") };
        TeamRoot = teamRoot;
        try
        {
            Folders = await Task.Run(() => FolderBrowser.List(BrowsingConfig, cancellation: cancellation), cancellation).ConfigureAwait(false);
            ScopeVerified = true;
            Message = Folders.Count == 0 ? "接続できました。直下にフォルダはありません。" : "見えるフォルダを確認して「次へ」を押してください。";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) when (teamRoot)
        {
            Draft = Draft with { Remote = own }; TeamRoot = false;
            Message = "チームの最上位を開けません。このアカウントでは使えない場合があります。「自分のフォルダ」に戻しました。";
            try
            {
                Folders = await Task.Run(() => FolderBrowser.List(BrowsingConfig, cancellation: cancellation), cancellation).ConfigureAwait(false);
                ScopeVerified = true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { Message += " 接続を確認して、もう一度お試しください。"; }
        }
        catch (Exception) { Message = "フォルダを確認できませんでした。接続と認可を確認して、もう一度お試しください。"; }
    }

    public void SetOptions(string mountPoint, string retention)
    {
        if (Step != SetupStep.Options) throw new InvalidOperationException("この PC での使い方の段を開いてください。");
        Draft = (Draft with { MountPoint = mountPoint, NetworkMode = true, Retention = retention }).Validate();
    }

    public void SetHidden(string[] paths) => Draft = (Draft with { HiddenPaths = paths }).Validate();

    public DbxConfig Commit()
    {
        if (Step != SetupStep.Finish || !Connected || !ScopeVerified)
            throw new InvalidOperationException("フォルダを確認し、最後の段まで進んでください。");
        PrivateStateFiles.EnsureParent(login.ConfigPath);
        DbxConfig saved = fileChange is null
            ? controller.ApplyConnectionChange(Original, _ => Draft with { WriteAllowedRemotes = [] })
            : fileChange.Commit(controller, Original, Draft);
        fileChange?.Dispose(); fileChange = null;
        Draft = saved; Committed = true;
        return saved;
    }

    public void CancelLogin() => authorizing?.Cancel();

    public void Dispose()
    {
        if (disposed) return;
        CancelLogin();
        if (authorization is { IsCompleted: false }) authorization.GetAwaiter().GetResult();
        fileChange?.Dispose(); fileChange = null;
        authorizing?.Dispose();
        disposed = true;
    }
}
