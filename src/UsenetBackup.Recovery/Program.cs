namespace UsenetBackup.Recovery;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Win95 Setup look for the USB/WinPE recovery environment: explicit flag
        // or auto-detected when running inside Windows PE. Skipping visual styles
        // is what makes every standard control render in the authentic classic
        // style; the wizard draws its own teal desktop and gradient title bar.
        bool win95 = args.Contains("--win95", StringComparer.OrdinalIgnoreCase)
            || Win95Theme.IsWinPE();

        if (win95)
        {
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
        }
        else
        {
            ApplicationConfiguration.Initialize();
        }

        Application.Run(new RecoveryWizard(win95));
    }
}
