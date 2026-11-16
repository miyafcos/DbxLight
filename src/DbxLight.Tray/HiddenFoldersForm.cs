using DbxLight.Core;

namespace DbxLight.Tray;

public sealed class HiddenFoldersForm : DbxForm
{
    private readonly TreeView tree = new() { CheckBoxes = true, Dock = DockStyle.Fill, HideSelection = false };
    private readonly Label changes = Ui.Label("表示しないにする 0 件、表示に戻す 0 件");
    private readonly Label message = Ui.Label("チェックありは表示、チェックなしは表示しない設定です。");
    private readonly Button apply;
    private readonly HiddenSelection selection;
    private readonly DbxConfig original;
    private readonly Func<Func<DbxConfig, DbxConfig>, Task> save;
    private readonly Action applied;
    private readonly HashSet<TreeNode> loading = [];
    private readonly HashSet<TreeNode> loaded = [];
    private bool updating;
    private Task? rootTask;
    public Task LoadedRoot => rootTask ??= LoadRootAsync();

    public HiddenFoldersForm(DbxConfig config, Func<Func<DbxConfig, DbxConfig>, Task> save, Action applied)
    {
        original = config; this.save = save; this.applied = applied; selection = new(config.HiddenPaths);
        Ui.Configure(this, "表示しないフォルダ", new Size(784, 592));
        var column = Ui.Column(6);
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.Controls.Add(Ui.Label("この PC の見え方だけを変えます。Dropbox 上のファイルと、ほかの人の見え方は変わりません。"), 0, 0);
        column.Controls.Add(message, 0, 1);
        column.Controls.Add(tree, 0, 2);
        column.Controls.Add(changes, 0, 3);
        column.Controls.Add(Ui.Label("表示しない場所と同じ名前のフォルダを、あとから作らないでください。\n件数は、選択したフォルダの数です。中のフォルダはまとめて扱います。"), 0, 4);
        apply = Ui.Button("適用", async (_, _) => await ApplyAsync());
        column.Controls.Add(Ui.Buttons(Ui.Button("閉じる", (_, _) => Close()), apply), 0, 5);
        Controls.Add(column);
        tree.BeforeExpand += async (_, e) => { if (e.Node is not null) await LoadChildrenAsync(e.Node); };
        tree.AfterCheck += (_, e) =>
        {
            if (updating || e.Node?.Tag is not string path || path.Length == 0) return;
            try { selection.SetShown(path, e.Node.Checked); message.Text = "変更は「適用」を押すまで反映されません。"; }
            catch (ArgumentException error) { message.Text = error.Message; }
            RefreshChecks(tree.Nodes);
            HiddenDiff diff = selection.Diff();
            changes.Text = $"表示しないにする {diff.ToHide.Count} 件、表示に戻す {diff.ToShow.Count} 件";
        };
        Load += (_, _) => { _ = LoadedRoot; };
        tree.BeforeCheck += (_, e) => { if (!updating && e.Node?.Tag is not string { Length: > 0 }) e.Cancel = true; };
    }
    private static TreeNode Placeholder() => new("読み込み中") { Tag = null };
    private TreeNode FolderNode(RemoteFolder folder)
    {
        var node = new TreeNode(folder.Name) { Tag = folder.Path, Checked = selection.IsShown(folder.Path) };
        node.Nodes.Add(Placeholder());
        return node;
    }
    public async Task LoadRootAsync()
    {
        tree.Nodes.Clear();
        var root = new TreeNode("Dropbox 上のフォルダ") { Tag = "", Checked = true };
        root.Nodes.Add(Placeholder()); tree.Nodes.Add(root);
        await LoadChildrenAsync(root);
        if (!IsDisposed && loaded.Contains(root)) root.Expand();
    }
    private async Task LoadChildrenAsync(TreeNode node)
    {
        if (node.Tag is not string path || loaded.Contains(node) || !loading.Add(node) || IsDisposed) return;
        message.Text = "読み込み中";
        try
        {
            IReadOnlyList<RemoteFolder> children = await Task.Run(() => FolderBrowser.List(original, path));
            if (IsDisposed) return;
            updating = true;
            try
            {
                node.Nodes.Clear();
                foreach (RemoteFolder folder in children) node.Nodes.Add(FolderNode(folder));
                loaded.Add(node);
            }
            finally { updating = false; }
            message.Text = children.Count == 0 ? "このフォルダの下にフォルダはありません。" : "チェックありは表示、チェックなしは表示しない設定です。";
        }
        catch (Exception e)
        {
            if (!IsDisposed)
            {
                node.Nodes.Clear(); node.Nodes.Add(new TreeNode("読み込めませんでした。開き直して、もう一度試してください。"));
                message.Text = Ui.Failure(e);
            }
        }
        finally { loading.Remove(node); }
    }
    private void RefreshChecks(TreeNodeCollection nodes)
    {
        updating = true;
        try
        {
            void Walk(TreeNodeCollection children)
            {
                foreach (TreeNode node in children)
                {
                    if (node.Tag is string path) node.Checked = path.Length == 0 || selection.IsShown(path);
                    Walk(node.Nodes);
                }
            }
            Walk(nodes);
        }
        finally { updating = false; }
    }
    private async Task ApplyAsync()
    {
        HiddenDiff diff = selection.Diff();
        if (diff.ToHide.Count + diff.ToShow.Count == 0) { message.Text = "変更はありません。"; return; }
        string confirmation = $"表示しないにする {diff.ToHide.Count} 件、表示に戻す {diff.ToShow.Count} 件\n一度つなぎ直して適用します。";
        if (MessageBox.Show(this, confirmation, "表示するフォルダの変更", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        apply.Enabled = false;
        string[] paths = selection.Paths.ToArray();
        try
        {
            await save(c =>
            {
                if (!selection.Matches(c.HiddenPaths) ||
                    !c.Remote.Equals(original.Remote, StringComparison.Ordinal) ||
                    !c.MountPoint.Equals(original.MountPoint, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(c.RcloneConfigPath, original.RcloneConfigPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("別の操作で設定が変わりました。この画面を開き直してください。");
                return c with { HiddenPaths = paths };
            });
            selection.Accept(); changes.Text = "表示しないにする 0 件、表示に戻す 0 件";
            message.Text = "表示しないフォルダを適用しました。"; applied();
        }
        catch (Exception e) { if (!IsDisposed) message.Text = Ui.Failure(e); }
        finally { if (!IsDisposed) apply.Enabled = true; }
    }
    public async Task ExpandFirstFolderAsync()
    {
        await LoadedRoot;
        if (tree.Nodes.Count == 0 || !loaded.Contains(tree.Nodes[0])) throw new IOException("フォルダツリーを読み込めませんでした。" + message.Text);
        TreeNode? folder = tree.Nodes[0].Nodes.Cast<TreeNode>().FirstOrDefault(n => n.Tag is string);
        if (folder is null) throw new IOException("試験用のフォルダが見つかりません。");
        await LoadChildrenAsync(folder);
        if (!loaded.Contains(folder) || folder.Nodes.Count == 0) throw new IOException("子フォルダを読み込めませんでした。" + message.Text);
        folder.Expand();
    }
}
