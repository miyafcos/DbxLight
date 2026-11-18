namespace DbxLight.Core;

/// <summary>Account operations share the lifecycle and destination guards with configuration changes.</summary>
public sealed class AccountSettings
{
    private readonly MountController controller;
    private readonly string dedicatedConfig;
    public AccountSettings(MountController controller, string? dedicatedConfig = null)
    {
        this.controller = controller;
        this.dedicatedConfig = CredentialSwitch.KeyPath(controller.Config.PathName);
        if (!Path.IsPathFullyQualified(this.dedicatedConfig)) throw new ArgumentException("Login config must be fully qualified.");
    }

    public async Task<DbxConfig> LogoutAsync(CancellationToken cancellation = default)
    {
        DbxConfig expected = controller.Config.Load();
        await Task.Run(() => controller.PrepareConnectionChange(expected), cancellation).ConfigureAwait(false);
        bool dedicated = expected.Remote.StartsWith("dbxlight:", StringComparison.Ordinal) &&
            string.Equals(expected.RcloneConfigPath, dedicatedConfig, StringComparison.OrdinalIgnoreCase);
        using PrivateLoginFile? change = dedicated ? new PrivateLoginFile(controller.Config, dedicatedConfig) : null;
        if (dedicated)
            await new RcloneLogin(expected.RclonePath, dedicatedConfig).DeleteAsync(change!.WorkingPath, cancellation).ConfigureAwait(false);
        DbxConfig after = DropboxCopyPaths.ClearBinding(expected) with { Remote = "",
            RcloneConfigPath = dedicated ? null : expected.RcloneConfigPath, WriteAllowedRemotes = [] };
        DbxConfig saved = await Task.Run(() => ControlGate.Execute(() =>
        {
            controller.PrepareConnectionChange(expected);
            return DropboxCopyPaths.CompleteLogout(controller.Config.PathName, () => change is null
                ? controller.ApplyConnectionChange(expected, _ => after)
                : change.Commit(controller, expected, after));
        }), cancellation).ConfigureAwait(false);
        SnapshotRenewalFile.RecoverAbandoned();
        CredentialSwitch.CleanupKnown(controller.Config.PathName, uncertainOrphans: false);
        CredentialSwitch.ClearRelogin(controller.Config.PathName);
        return saved;
    }
}
