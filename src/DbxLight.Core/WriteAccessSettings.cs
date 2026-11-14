namespace DbxLight.Core;

public sealed class WriteAccessSettings(MountController controller)
{
    public DbxConfig SetEnabled(DbxConfig expected, bool enabled, bool confirmed)
    {
        if (enabled && !confirmed) throw new InvalidOperationException("書き込みを有効にする前に、確認の窓で内容を確認してください。");
        if (string.IsNullOrWhiteSpace(expected.Remote)) throw new InvalidOperationException("先に接続先を設定してください。");
        return controller.ApplyConnectionChange(expected, c => c with { WriteAllowedRemotes = enabled ? [c.Remote] : [] });
    }
}
