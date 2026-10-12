using System.Runtime.InteropServices;

namespace UsenetBackup.Gui;

/// <summary>
/// FileKeep native Windows GUI: a WebView2 shell hosting the FileKeep
/// dashboard served by the local service. Provides native Windows folder
/// dialogs via a JavaScript bridge so users never have to type paths.
/// </summary>
internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
