using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit6iTests
{
    private static void ExternalPreparationStop()
    {
        using var f = new Unit5Fixture("u6i-external-preparation");
        bool stopped = false; int resumes = 0;
        RunTray(f, async c =>
        {
            await FormTests.Field<Task>(c, "mountStartup");
            TestRunner.Assert(stopped);
            TestRunner.Equal(0, resumes);
            TestRunner.Equal(ResidentPhase.Stopped, c.Phase);
            TestRunner.Assert(c.Snapshot.Error is null);
            TestRunner.Assert(FormTests.Field<string?>(c, "startupError") is null);
            TestRunner.Equal("\u518d\u958b", Pause(c).Text);
            TestRunner.Assert(Pause(c).Enabled && FormTests.Field<System.Windows.Forms.Timer>(c, "poll").Enabled);
            await ResumeAsync(c);
            TestRunner.Equal(ResidentPhase.Watching, c.Phase);
            TestRunner.Assert(c.Snapshot.Status.Ready);
            TestRunner.Equal(1, resumes);
        }, (c, execute) =>
        {
            var dispatcher = FormTests.Field<ResidentWindow>(c, "dispatcher");
            c.Controller.StartupCheckpoint = (stage, state) =>
            {
                if (stage == "after-resume") resumes++;
                if (stage != "after-cache-preservation" || stopped) return;
                var reply = f.Cli("stop");
                TestRunner.Equal(0, reply.Exit);
                stopped = true;
                dispatcher.BeginInvoke((Action)(() => { _ = execute(); }));
            };
        }, autoStart: false);
    }
}
