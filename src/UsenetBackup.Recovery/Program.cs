namespace UsenetBackup.Recovery;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Win95 Setup look for the USB/WinRE recovery environment: explicit flag
        // or auto-detected when running inside Windows PE. Skipping visual styles
        // is what makes every standard control render in the authentic classic
        // style; the wizard draws its own teal desktop and gradient title bar.
        bool win95 = args.Contains("--win95", StringComparer.OrdinalIgnoreCase)
            || Win95Theme.IsWinRE();

        if (win95)
        {
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
        }
        else
        {
            ApplicationConfiguration.Initialize();
        }

        // NS-2: in WinPE this app is the ONLY recovery UI — a stray
        // exception must not kill it behind the stock .NET crash dialog.
        // Catch UI-thread exceptions, show the error in a plain dialog,
        // and keep the wizard alive; log domain-level fatals the same
        // way on a best-effort basis before the process goes down.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            try
            {
                MessageBox.Show(
                    "Something went wrong, but you can keep going:\n\n" + e.Exception.Message,
                    "FileKeep Recovery", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch { /* the dialog itself failed; swallow and continue */ }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                if (e.ExceptionObject is Exception ex)
                    MessageBox.Show(
                        "A serious error occurred:\n\n" + ex.Message,
                        "FileKeep Recovery", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { /* best effort only */ }
        };

        Application.Run(new RecoveryWizard(win95));
    }
}
