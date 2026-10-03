namespace UsenetBackup.Recovery;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new RecoveryWizard());
    }
}
