using System.Runtime.InteropServices;

namespace FileKeep.Gui;

/// <summary>
/// FileKeep native Windows GUI: a WebView2 shell hosting the FileKeep
/// dashboard served by the local service. Provides native Windows folder
/// dialogs via a JavaScript bridge so users never have to type paths.
/// </summary>
internal static class Program
{
    private const string MutexName = @"Global\FileKeepGuiSingleInstance";
    private const string WindowTitle = "FileKeep";

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    [STAThread]
    static void Main()
    {
        // P2-L12: when the previous GUI instance crashed, the mutex is
        // abandoned and the ownership request in the constructor throws
        // AbandonedMutexException — ownership IS granted in that case,
        // but unhandled it crashed every later launch. Treat it as
        // acquisition (we are the only instance; the last one died).
        Mutex mutex;
        bool createdNew;
        try
        {
            mutex = new Mutex(true, MutexName, out createdNew);
        }
        catch (AbandonedMutexException)
        {
            mutex = new Mutex(false, MutexName);
            createdNew = true;
        }
        using var ownedMutex = mutex;
        if (!createdNew)
        {
            // Another instance is already running — bring its window to front and exit.
            IntPtr hWnd = FindWindow(null, WindowTitle);
            if (hWnd != IntPtr.Zero)
            {
                ShowWindow(hWnd, SW_RESTORE);
                SetForegroundWindow(hWnd);
            }
            return;
        }

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }
}
