using System.Reflection;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static class FormTests
{
    internal static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    internal static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { failure = e; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        TestRunner.Assert(thread.Join(TimeSpan.FromSeconds(15)), "Form test did not terminate.");
        if (failure is not null) throw new InvalidOperationException("Form test failed.", failure);
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }

    public static void Permissions() => Sta(() =>
    {
        var readOnly = new DbxConfig { Remote = "local:" };
        var writable = readOnly with { WriteAllowedRemotes = [readOnly.Remote] };
        var snapshot = new TraySnapshot(new() { Running = true, Ready = true }, [], [], true, null, ActiveConfig: readOnly);
        string root = Path.Combine(UnitTests.RepositoryRoot(), "_test-work", "permissions-" + Guid.NewGuid().ToString("N"));
        var options = new AppOptions(Path.Combine(root, "config.json"), Path.Combine(root, "state.json"));
        using var settings = new SettingsForm(readOnly,
            new RegistrationService(RegistrationService.TestHive + "\\Permissions-" + Guid.NewGuid().ToString("N")),
            Environment.ProcessPath!, options, _ => Task.CompletedTask);
        TestRunner.Assert(Descendants(settings).OfType<Label>().Any(label => label.Text.Contains("\u8aad\u307f\u53d6\u308a\u5c02\u7528", StringComparison.Ordinal)),
            "Settings has no visible read-only indication.");
        MethodInfo update = typeof(SettingsForm).GetMethod("UpdateSnapshot") ?? throw new InvalidOperationException("Settings cannot update its active permission.");
        update.Invoke(settings, [snapshot, writable]);
        string text = Field<Label>(settings, "permission").Text;
        TestRunner.Assert(text.Contains("\u8aad\u307f\u53d6\u308a\u5c02\u7528", StringComparison.Ordinal) && !text.Contains("\u66f8\u304d\u8fbc\u307f\u53ef", StringComparison.Ordinal));
        Field<TextBox>(settings, "remote").Text = "not-allowed:";
        TestRunner.Equal(text, Field<Label>(settings, "permission").Text);
        TestRunner.Assert(Descendants(settings).OfType<Label>().Any(label => label.Text.Contains("\u4e00\u89a7", StringComparison.Ordinal) && label.Text.Contains("\u8aad\u307f\u53d6\u308a\u5c02\u7528", StringComparison.Ordinal)),
            "Settings omits the remote allow-list explanation.");
        using var status = new StatusForm();
        status.UpdateSnapshot(snapshot with { ActiveConfig = writable }, null, readOnly);
        TestRunner.Assert(Field<Label>(status, "summary").Text.Split('\n')[0].Contains("\u66f8\u304d\u8fbc\u307f\u53ef", StringComparison.Ordinal));
        update.Invoke(settings, [snapshot with { ActiveConfig = writable }, readOnly]);
        TestRunner.Assert(Field<Label>(settings, "permission").Text.Contains("\u66f8\u304d\u8fbc\u307f\u53ef", StringComparison.Ordinal));
        var confirming = snapshot with { Confirming = true };
        status.UpdateSnapshot(confirming, null, readOnly);
        text = Field<Label>(status, "summary").Text.Split('\n')[0];
        TestRunner.Assert(text.Contains("\u78ba\u8a8d\u4e2d", StringComparison.Ordinal) && !text.Contains("\u8aad\u307f\u53d6\u308a\u5c02\u7528", StringComparison.Ordinal));
        update.Invoke(settings, [confirming, readOnly]);
        text = Field<Label>(settings, "permission").Text;
        TestRunner.Assert(text.Contains("\u78ba\u8a8d\u4e2d", StringComparison.Ordinal) && !text.Contains("\u8aad\u307f\u53d6\u308a\u5c02\u7528", StringComparison.Ordinal));
    });

    public static void StatusHeadline() => Sta(() =>
    {
        using var form = new StatusForm();
        var config = new DbxConfig { Remote = "local:" };
        var busy = new TraySnapshot(new() { Running = true, Ready = true, UploadsQueued = 3,
            UploadsInProgress = 2, ErroredFiles = 0 }, [], [], true, null, ActiveConfig: config);
        form.UpdateSnapshot(busy, null, config);
        string[] rows = Field<Label>(form, "summary").Text.Split('\n');
        TestRunner.Assert(rows[0].Contains("\u30a2\u30c3\u30d7\u30ed\u30fc\u30c9\u4e2d", StringComparison.Ordinal) && !rows[0].Any(char.IsDigit),
            "Headline repeats a count instead of the overall uploading state: " + rows[0]);
        TestRunner.Assert(rows[1].Contains("3") && rows[1].Contains("2"));
        form.UpdateSnapshot(busy with { Status = busy.Status with { UploadsQueued = 0, UploadsInProgress = 0 }, Errors = ["ERROR other"] }, null, config);
        TestRunner.Assert(Field<Label>(form, "summary").Text.Split('\n')[0].Contains("\u6ce8\u610f", StringComparison.Ordinal));
        form.UpdateSnapshot(busy with { Status = busy.Status with { ErroredFiles = 4 } }, null, config);
        rows = Field<Label>(form, "summary").Text.Split('\n');
        TestRunner.Assert(rows[0].Contains("\u5931\u6557", StringComparison.Ordinal) && !rows[0].Any(char.IsDigit));
    });
}
