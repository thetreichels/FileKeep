using System.Runtime.InteropServices;

namespace UsenetBackup.Service;

/// <summary>
/// Minimal Windows Service Control Manager host, written against raw
/// advapi32 P/Invoke so the service needs no extra NuGet packages.
/// On non-Windows this throws <see cref="PlatformNotSupportedException"/>
/// (use console mode there).
/// </summary>
public static class WindowsServiceHost
{
    private const string Advapi32 = "advapi32.dll";

    private const int SERVICE_WIN32_OWN_PROCESS = 0x00000010;
    private const int SERVICE_START_PENDING = 0x00000002;
    private const int SERVICE_STOP_PENDING = 0x00000003;
    private const int SERVICE_RUNNING = 0x00000004;
    private const int SERVICE_STOPPED = 0x00000001;
    private const int SERVICE_ACCEPT_STOP = 0x00000001;
    private const int SERVICE_ACCEPT_SHUTDOWN = 0x00000004;
    private const int SERVICE_CONTROL_STOP = 0x00000001;
    private const int SERVICE_CONTROL_SHUTDOWN = 0x00000005;
    private const int NO_ERROR = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public int dwServiceType;
        public int dwCurrentState;
        public int dwControlsAccepted;
        public int dwWin32ExitCode;
        public int dwServiceSpecificExitCode;
        public int dwCheckPoint;
        public int dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SERVICE_TABLE_ENTRY
    {
        public string? lpServiceName;
        public ServiceMainDelegate? lpServiceProc;
    }

    private delegate void ServiceMainDelegate(int dwArgc, IntPtr lpszArgv);
    private delegate int ServiceCtrlHandlerDelegate(int dwControl, int dwEventType, IntPtr lpEventData, IntPtr lpContext);

    [DllImport(Advapi32, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartServiceCtrlDispatcher([In] SERVICE_TABLE_ENTRY[] lpServiceTable);

    [DllImport(Advapi32, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(string lpServiceName, ServiceCtrlHandlerDelegate lpHandlerProc, IntPtr lpContext);

    [DllImport(Advapi32, SetLastError = true)]
    private static extern bool SetServiceStatus(IntPtr hServiceStatus, ref SERVICE_STATUS lpServiceStatus);

    private static IntPtr _statusHandle;
    private static CancellationTokenSource? _cts;
    private static readonly ServiceCtrlHandlerDelegate _handler = Handler;
    private static readonly ServiceMainDelegate _serviceMain = ServiceMain;

    /// <summary>
    /// Registers with the SCM and runs <paramref name="runAsync"/> until the
    /// service is stopped. This call blocks; it never returns normally.
    /// </summary>
    public static void Run(string serviceName, Func<CancellationToken, Task> runAsync)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Running as a Windows service requires Windows. Use --console on other platforms.");

        _runAsync = runAsync ?? throw new ArgumentNullException(nameof(runAsync));
        _serviceName = serviceName ?? throw new ArgumentNullException(nameof(serviceName));

        var table = new SERVICE_TABLE_ENTRY[2];
        table[0] = new SERVICE_TABLE_ENTRY { lpServiceName = serviceName, lpServiceProc = _serviceMain };
        table[1] = new SERVICE_TABLE_ENTRY { lpServiceName = null, lpServiceProc = null };

        if (!StartServiceCtrlDispatcher(table))
            throw new InvalidOperationException(
                $"StartServiceCtrlDispatcher failed (win32 error {Marshal.GetLastWin32Error()}). " +
                "This executable must be started by the Service Control Manager, or run with --console.");
    }

    private static Func<CancellationToken, Task>? _runAsync;
    private static string? _serviceName;

    private static void ServiceMain(int dwArgc, IntPtr lpszArgv)
    {
        _statusHandle = RegisterServiceCtrlHandlerEx(_serviceName!, _handler, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero)
            return;

        ReportStatus(SERVICE_START_PENDING, SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN, 3000);
        _cts = new CancellationTokenSource();

        try
        {
            ReportStatus(SERVICE_RUNNING, SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN, 0);
            _runAsync!(_cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // The scheduler already logs job failures; an unhandled exception
            // here means the host itself died.
        }
        finally
        {
            ReportStatus(SERVICE_STOPPED, 0, 0);
        }
    }

    private static int Handler(int dwControl, int dwEventType, IntPtr lpEventData, IntPtr lpContext)
    {
        if (dwControl is SERVICE_CONTROL_STOP or SERVICE_CONTROL_SHUTDOWN)
        {
            ReportStatus(SERVICE_STOP_PENDING, 0, 5000);
            _cts?.Cancel();
        }
        return NO_ERROR;
    }

    private static void ReportStatus(int state, int controlsAccepted, int waitHint)
    {
        if (_statusHandle == IntPtr.Zero)
            return;
        var status = new SERVICE_STATUS
        {
            dwServiceType = SERVICE_WIN32_OWN_PROCESS,
            dwCurrentState = state,
            dwControlsAccepted = controlsAccepted,
            dwWin32ExitCode = NO_ERROR,
            dwServiceSpecificExitCode = 0,
            dwCheckPoint = 0,
            dwWaitHint = waitHint,
        };
        SetServiceStatus(_statusHandle, ref status);
    }
}
