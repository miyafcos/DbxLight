namespace DbxLight.Tray;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetDefaultFont(Ui.AppFont);
        try
        {
            AppOptions options = AppOptions.Parse(args);
            if (options.SmokeOutput is not null) return SmokeRunner.Run(options);
            if (options.CopyPathServer) return ShellCopyServer.Run(options);
            if (options.Registration is not null)
                return ApplyRegistration(options, new RegistrationService(options.RegistryRoot));
            using var instance = new SingleInstance(options.StatePath);
            if (!instance.IsFirst) { instance.ForwardAsync(args).GetAwaiter().GetResult(); return 0; }
            using var context = new ResidentContext(options, instance);
            Application.Run(context);
            return Environment.ExitCode;
        }
        catch (Exception e)
        {
            // No modal error dialog: command-line tests must always terminate unattended.
            System.Diagnostics.Trace.WriteLine(e);
            if (args.Contains("--register") || args.Contains("--unregister"))
                Console.Error.WriteLine(RegistrationFailure("登録の操作", e));
            return 1;
        }
    }

    private static string RegistrationFailure(string operation, Exception error) =>
        operation + "に失敗: " + Ui.Failure(error).Replace('\r', ' ').Replace('\n', ' ');

    internal static int ApplyRegistration(AppOptions options, RegistrationService registry)
    {
        if (options.Registration == "--register")
        {
            try
            {
                var config = new DbxLight.Core.ConfigStore(options.ConfigPath).Load();
                registry.InstallShell(config, Environment.ProcessPath!, options);
                registry.SetAutoStart(true, Environment.ProcessPath!, options);
                registry.InstallNavigation(config, Environment.ProcessPath!);
                return 0;
            }
            catch (Exception error)
            {
                System.Diagnostics.Trace.WriteLine(error);
                Console.Error.WriteLine(RegistrationFailure("登録", error));
                return 1;
            }
        }
        int failures = 0;
        foreach (var step in new (string Name, Action Undo)[]
        {
            ("右クリックの解除", registry.UninstallShell),
            ("自動起動の解除", () => registry.SetAutoStart(false, Environment.ProcessPath!, options)),
            ("左の一覧の解除", registry.UninstallNavigation)
        })
        {
            try { step.Undo(); }
            catch (Exception error)
            {
                failures++;
                System.Diagnostics.Trace.WriteLine(error);
                Console.Error.WriteLine(RegistrationFailure(step.Name, error));
            }
        }
        return failures == 0 ? 0 : 1;
    }
}
