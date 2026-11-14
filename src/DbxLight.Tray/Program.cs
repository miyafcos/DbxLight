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
            if (options.Registration is not null)
            {
                var registry = new RegistrationService(options.RegistryRoot);
                if (options.Registration == "--register")
                {
                    registry.InstallShell(new DbxLight.Core.ConfigStore(options.ConfigPath).Load(), Environment.ProcessPath!, options);
                    registry.SetAutoStart(true, Environment.ProcessPath!, options);
                }
                else { registry.UninstallShell(); registry.SetAutoStart(false, Environment.ProcessPath!, options); }
                return 0;
            }
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
            return 1;
        }
    }
}
