using System.Windows;
using WixToolset.Mba.Core;

namespace FileKeep.BA;

/// <summary>
/// Custom managed bootstrapper application for the FileKeep installer.
/// Replaces WixStandardBootstrapperApplication to support system light/dark
/// theme following. Handles the standard Burn lifecycle: detect, plan,
/// apply, with a simple splash-style UI (Install → Progress → Success/Failure).
/// </summary>
public sealed class FileKeepBootstrapperApplication : BootstrapperApplication
{
    private MainWindow? _mainWindow;

    protected override void Run()
    {
        // Apply the system theme before showing any UI.
        ThemeManager.ApplySystemTheme();

        _mainWindow = new MainWindow(this);
        _mainWindow.Closed += (_, _) => Engine.Quit(0);

        // Start detection to determine install vs. modify mode.
        Engine.Detect();

        _mainWindow.Show();
        // The WPF dispatcher runs the message loop; Burn engine events
        // arrive on this thread via the managed host.
        System.Windows.Threading.Dispatcher.Run();
    }

    protected override void OnDetectComplete(DetectCompleteEventArgs args)
    {
        // TODO: Update UI based on detection (installed version, etc.)
        base.OnDetectComplete(args);
    }

    protected override void OnPlanComplete(PlanCompleteEventArgs args)
    {
        base.OnPlanComplete(args);
    }

    protected override void OnExecuteProgress(ExecuteProgressEventArgs args)
    {
        _mainWindow?.UpdateProgress(args.ProgressPercentage, args.OverallPercentage);
        base.OnExecuteProgress(args);
    }

    protected override void OnExecuteComplete(ExecuteCompleteEventArgs args)
    {
        _mainWindow?.ShowResult(args.Status == 0);
        base.OnExecuteComplete(args);
    }

    /// <summary>Called by the UI when the user clicks Install.</summary>
    public void StartInstall()
    {
        Engine.Plan(LaunchAction.Install);
    }

    protected override void OnPlanBegin(PlanBeginEventArgs args)
    {
        base.OnPlanBegin(args);
    }

    protected override void OnApplyBegin(ApplyBeginEventArgs args)
    {
        _mainWindow?.ShowProgress();
        base.OnApplyBegin(args);
    }
}
