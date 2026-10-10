// FileKeepVss.exe — Minimal VSS shadow copy helper for FileKeep.
//
// Creates a VSS shadow copy of a volume, prints the snapshot device path
// to stdout, waits for a completion signal on stdin (or timeout), then
// finalizes the VSS backup session and deletes the snapshot.
//
// Protocol:
//   FileKeepVss.exe --volume C: [--timeout 3600]
//   → stdout: \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy3\n
//   → stdin:  "complete\n" → BackupComplete, delete snapshot, exit 0
//       (exit 4 if BackupComplete itself failed — data intact, writers not finalized)
//   → stdin:  "abort\n"    → AbortBackup, delete snapshot, exit 3
//   → stdin:  EOF          → AbortBackup, delete snapshot, exit 3
//   → timeout              → AbortBackup, delete snapshot, exit 2
//   → any VSS failure      → exit 1 with error on stderr
//
// VSS backup protocol implemented:
//   CreateVssBackupComponents → InitializeForBackup → SetContext(VSS_CTX_BACKUP)
//   → GatherWriterMetadata → SetBackupState(FULL) → StartSnapshotSet
//   → AddToSnapshotSet → PrepareForBackup → DoSnapshotSet
//   → (engine copies data from snapshot)
//   → BackupComplete (on "complete") or AbortBackup (on "abort"/EOF/timeout)
//   → DeleteSnapshots
//
// BackupComplete/AbortBackup are the writer finalization calls that pair
// with PrepareForBackup. Without them, writers (SQL Server, etc.) are left
// in a dangling "backup in progress" state: transaction logs are never
// truncated and subsequent backups may misbehave.
//
// This is intentionally minimal: no dependencies beyond the Windows SDK.
// The backup engine (managed) spawns this process and never touches COM itself.

#define _WIN32_DCOM
#include <windows.h>
#include <vss.h>
#include <vswriter.h>
#include <vsbackup.h>

#include <cctype>
#include <cstring>
#include <new>
#include <cstdio>
#include <string>
#include <chrono>

// Link against VSS API
#pragma comment(lib, "VssApi.lib")

namespace {

void PrintUsage() {
    fprintf(stderr,
        "Usage: FileKeepVss.exe --volume <drive> [--timeout <seconds>]\n"
        "\n"
        "Creates a VSS shadow copy, prints the snapshot device path to stdout,\n"
        "waits for a completion signal on stdin (or timeout), finalizes the\n"
        "VSS backup session, then deletes the snapshot.\n"
        "\n"
        "  --volume   Volume to snapshot (e.g., C: or C:\\)\n"
        "  --timeout  Seconds to hold the snapshot (default: 3600)\n"
        "\n"
        "Completion signals (stdin):\n"
        "  complete   Backup succeeded → BackupComplete, exit 0\n"
        "  abort      Backup failed    → AbortBackup, exit 3\n"
        "  EOF        Treated as abort (fail-safe)\n"
        "\n"
        "Exit codes:\n"
        "  0  Snapshot created, backup completed, cleaned up\n"
        "  1  VSS error (see stderr)\n"
        "  2  Timeout waiting for signal (backup aborted, cleaned up)\n"
        "  3  Abort signal or EOF (backup aborted, cleaned up)\n");
}

// Waits for an async VSS job to complete.
HRESULT WaitForAsync(IVssAsync* async) {
    if (!async) return E_INVALIDARG;
    HRESULT hr = async->Wait();
    if (FAILED(hr)) return hr;
    HRESULT result = S_OK;
    hr = async->QueryStatus(&result, nullptr);
    if (FAILED(hr)) return hr;
    return result;
}

// Converts a drive letter like "C:" or "C:\" to a volume path "C:\".
std::wstring NormalizeVolume(const std::wstring& input) {
    if (input.empty()) return L"";
    std::wstring vol = input;
    // Ensure trailing backslash
    if (vol.back() != L'\\') vol += L'\\';
    return vol;
}

// Completion signal outcomes.
enum class SignalResult {
    Complete,   // engine sent "complete"
    Abort,      // engine sent "abort" or unrecognized input
    Eof,        // stdin closed without a signal (fail-safe abort)
    Timeout,    // timed out waiting (fail-safe abort)
    Error,      // wait/read error (fail-safe abort)
};

// Shared state between the stdin reader thread and the main thread.
// Heap-allocated: on timeout the reader thread may still be blocked in
// ReadFile, so the state is intentionally leaked in that path — the
// process proceeds to abort and exit, which reclaims it.
struct StdinSignalState {
    HANDLE doneEvent = nullptr; // manual-reset; set when the read finishes
    char line[256] = {};        // bytes read (NUL-terminated)
    bool gotData = false;       // true if at least one byte was read
};

// Blocking stdin read. Anonymous pipe handles are NOT waitable objects —
// WaitForSingleObject must never be called on them (a failed wait would
// take the abort path and delete the snapshot mid-backup). A blocking
// ReadFile IS well-defined for pipes: it returns when the engine writes
// the signal or closes the pipe (EOF).
static DWORD WINAPI StdinReaderThread(LPVOID param) {
    auto* state = static_cast<StdinSignalState*>(param);
    HANDLE hStdin = GetStdHandle(STD_INPUT_HANDLE);
    if (hStdin && hStdin != INVALID_HANDLE_VALUE) {
        // Read until newline, EOF, or buffer full. A single ReadFile on a
        // byte-stream pipe is not guaranteed to return the whole line.
        size_t total = 0;
        while (total < sizeof(state->line) - 1) {
            DWORD bytesRead = 0;
            BOOL ok = ReadFile(hStdin, state->line + total,
                static_cast<DWORD>(sizeof(state->line) - 1 - total),
                &bytesRead, nullptr);
            if (!ok || bytesRead == 0)
                break; // EOF or error: engine went away
            total += bytesRead;
            state->line[total] = '\0';
            if (strchr(state->line, '\n') != nullptr)
                break;
        }
        state->gotData = total > 0;
    }
    SetEvent(state->doneEvent);
    return 0;
}

// Waits for the engine's completion signal on stdin.
// Only SignalResult::Complete means the backup succeeded; every other
// outcome must lead to AbortBackup (fail-safe).
//
// Synchronization design: a reader thread blocks in ReadFile on stdin
// while the main thread waits on a manual-reset event with the timeout.
// The event — not the pipe handle — is the waitable object, so there is
// no dependence on pipe-handle wait semantics at all.
SignalResult WaitForCompletionSignal(DWORD timeoutSecs) {
    HANDLE hStdin = GetStdHandle(STD_INPUT_HANDLE);
    if (hStdin == nullptr || hStdin == INVALID_HANDLE_VALUE) {
        fwprintf(stderr, L"No stdin handle; aborting backup\n");
        return SignalResult::Error;
    }

    // Guard against DWORD overflow in ms conversion.
    DWORD waitMs = (timeoutSecs > 4000000) ? INFINITE : timeoutSecs * 1000;

    auto* state = new (std::nothrow) StdinSignalState();
    if (!state) {
        fwprintf(stderr, L"Out of memory; aborting backup\n");
        return SignalResult::Error;
    }
    state->doneEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!state->doneEvent) {
        fwprintf(stderr, L"CreateEvent failed: %lu; aborting backup\n",
            GetLastError());
        delete state;
        return SignalResult::Error;
    }

    HANDLE hThread = CreateThread(nullptr, 0, StdinReaderThread, state, 0, nullptr);
    if (!hThread) {
        fwprintf(stderr, L"CreateThread failed: %lu; aborting backup\n",
            GetLastError());
        CloseHandle(state->doneEvent);
        delete state;
        return SignalResult::Error;
    }
    CloseHandle(hThread); // synchronize via doneEvent; the thread needs no join

    DWORD waitResult = WaitForSingleObject(state->doneEvent, waitMs);
    if (waitResult == WAIT_TIMEOUT) {
        fwprintf(stderr, L"Timeout (%lu seconds) waiting for completion signal; "
            L"aborting backup\n", timeoutSecs);
        // The reader thread is still blocked in ReadFile; its state is
        // intentionally leaked — the abort path exits the process shortly.
        CloseHandle(state->doneEvent);
        return SignalResult::Timeout;
    }
    CloseHandle(state->doneEvent);
    if (waitResult != WAIT_OBJECT_0) {
        fwprintf(stderr, L"Wait for completion signal failed: %lu; aborting backup\n",
            GetLastError());
        delete state;
        return SignalResult::Error;
    }

    // The reader thread finished writing before signaling the event, so
    // the state is safe to read and free here.
    bool gotData = state->gotData;
    std::string signal(state->line);
    delete state;

    if (!gotData) {
        // EOF: the engine went away without signaling. Fail-safe abort.
        fwprintf(stderr, L"Stdin closed without completion signal; aborting backup\n");
        return SignalResult::Eof;
    }

    for (auto& c : signal) c = static_cast<char>(std::tolower(
        static_cast<unsigned char>(c)));
    if (signal.find("complete") != std::string::npos) {
        return SignalResult::Complete;
    }
    // "abort" or unrecognized input → abort.
    return SignalResult::Abort;
}

int Run(const std::wstring& volume, DWORD timeoutSecs) {
    HRESULT hr;

    // Initialize COM
    hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr)) {
        fwprintf(stderr, L"CoInitializeEx failed: 0x%08X\n", hr);
        return 1;
    }

    IVssBackupComponents* backup = nullptr;
    VSS_ID snapshotSetId = GUID_NULL;
    VSS_ID snapshotId = GUID_NULL;
    bool snapshotCreated = false;
    bool writersEngaged = false;  // true once PrepareForBackup succeeds
    bool snapshotSetStarted = false;  // true once StartSnapshotSet succeeds

    // AbortBackup releases the in-progress backup session. Must be called
    // on any failure path after StartSnapshotSet succeeds — otherwise VSS
    // keeps the snapshot set "in progress" and subsequent runs fail with
    // VSS_E_SNAPSHOT_SET_IN_PROGRESS (0x80042316). After PrepareForBackup
    // it additionally releases writers from the backup session.
    auto abortBackup = [&]() {
        if (snapshotSetStarted && backup) {
            HRESULT ahr = backup->AbortBackup();
            if (FAILED(ahr)) {
                fwprintf(stderr, L"Warning: AbortBackup failed: 0x%08X\n", ahr);
            }
            snapshotSetStarted = false;
            writersEngaged = false;
        }
    };

    // Legacy alias: aborts the writer session. Prefer abortBackup(), which
    // also covers the pre-writer window between StartSnapshotSet and
    // PrepareForBackup.
    auto abortWriters = [&]() {
        abortBackup();
    };

    auto deleteSnapshot = [&]() {
        if (snapshotCreated && backup) {
            LONG deleted = 0;
            VSS_ID nonDeletedId = GUID_NULL;
            HRESULT dhr = backup->DeleteSnapshots(
                snapshotId,
                VSS_OBJECT_SNAPSHOT,
                FALSE,  // not force
                &deleted,
                &nonDeletedId);
            if (FAILED(dhr)) {
                fwprintf(stderr, L"Warning: DeleteSnapshots failed: 0x%08X\n", dhr);
            }
            snapshotCreated = false;
        }
    };

    // Ensure cleanup on any exit path. Note: this does NOT call
    // AbortBackup — callers must finalize the writer session explicitly
    // via BackupComplete or abortWriters() before cleanup().
    auto cleanup = [&]() {
        deleteSnapshot();
        if (backup) backup->Release();
        CoUninitialize();
    };

    // Create the VSS backup components object
    hr = CreateVssBackupComponents(&backup);
    if (FAILED(hr)) {
        fwprintf(stderr, L"CreateVssBackupComponents failed: 0x%08X\n", hr);
        cleanup();
        return 1;
    }

    // Initialize for backup
    hr = backup->InitializeForBackup();
    if (FAILED(hr)) {
        fwprintf(stderr, L"InitializeForBackup failed: 0x%08X\n", hr);
        cleanup();
        return 1;
    }

    // Use backup context (not client-accessible; we expose via device path)
    hr = backup->SetContext(VSS_CTX_BACKUP);
    if (FAILED(hr)) {
        fwprintf(stderr, L"SetContext failed: 0x%08X\n", hr);
        cleanup();
        return 1;
    }

    // Gather writer metadata (standard protocol step; enables future
    // writer status reporting)
    {
        IVssAsync* async = nullptr;
        hr = backup->GatherWriterMetadata(&async);
        if (SUCCEEDED(hr)) {
            hr = WaitForAsync(async);
            async->Release();
        }
        if (FAILED(hr)) {
            fwprintf(stderr, L"GatherWriterMetadata failed: 0x%08X\n", hr);
            cleanup();
            return 1;
        }
    }

    // Declare this a full backup so writers know to truncate logs etc.
    // (select components, bootable system state, FULL, no partial files)
    hr = backup->SetBackupState(TRUE, TRUE, VSS_BT_FULL, FALSE);
    if (FAILED(hr)) {
        fwprintf(stderr, L"SetBackupState failed: 0x%08X\n", hr);
        cleanup();
        return 1;
    }

    // Start a new snapshot set
    hr = backup->StartSnapshotSet(&snapshotSetId);
    if (FAILED(hr)) {
        fwprintf(stderr, L"StartSnapshotSet failed: 0x%08X\n", hr);
        cleanup();
        return 1;
    }
    snapshotSetStarted = true;

    // Add the volume to the set
    hr = backup->AddToSnapshotSet(
        const_cast<LPWSTR>(volume.c_str()),
        GUID_NULL,  // no specific provider
        &snapshotId);
    if (FAILED(hr)) {
        fwprintf(stderr, L"AddToSnapshotSet failed for %ls: 0x%08X\n",
            volume.c_str(), hr);
        abortBackup();
        cleanup();
        return 1;
    }

    // Prepare for backup (notifies writers). From this point on, the
    // writer session MUST be finalized with BackupComplete or AbortBackup.
    {
        IVssAsync* async = nullptr;
        hr = backup->PrepareForBackup(&async);
        if (SUCCEEDED(hr)) {
            hr = WaitForAsync(async);
            async->Release();
        }
        if (FAILED(hr)) {
            fwprintf(stderr, L"PrepareForBackup failed: 0x%08X\n", hr);
            abortBackup();
            cleanup();
            return 1;
        }
    }
    writersEngaged = true;

    // Create the snapshot
    {
        IVssAsync* async = nullptr;
        hr = backup->DoSnapshotSet(&async);
        if (SUCCEEDED(hr)) {
            hr = WaitForAsync(async);
            async->Release();
        }
        if (FAILED(hr)) {
            fwprintf(stderr, L"DoSnapshotSet failed: 0x%08X\n", hr);
            abortWriters();
            cleanup();
            return 1;
        }
    }
    snapshotCreated = true;

    // Get the snapshot device path
    VSS_SNAPSHOT_PROP prop = {};
    hr = backup->GetSnapshotProperties(snapshotId, &prop);
    if (FAILED(hr)) {
        fwprintf(stderr, L"GetSnapshotProperties failed: 0x%08X\n", hr);
        abortWriters();
        cleanup();
        return 1;
    }

    // Print the device path to stdout (the protocol)
    // Format: \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN
    wprintf(L"%ls\n", prop.m_pwszSnapshotDeviceObject);
    fflush(stdout);

    VssFreeSnapshotProperties(&prop);

    // Wait for the engine to finish copying data from the snapshot.
    SignalResult signal = WaitForCompletionSignal(timeoutSecs);

    if (signal == SignalResult::Complete) {
        // Finalize the writer session: writers can now truncate logs etc.
        IVssAsync* async = nullptr;
        hr = backup->BackupComplete(&async);
        if (SUCCEEDED(hr)) {
            hr = WaitForAsync(async);
            async->Release();
        }
        writersEngaged = false;
        bool finalizeFailed = FAILED(hr);
        if (finalizeFailed) {
            // Fail closed: the backup data is intact, but writer
            // finalization failed (logs not truncated, writers may be in a
            // bad state). Delete the snapshot, but report the failure with
            // a distinct exit code so the managed engine throws from
            // Complete() instead of seeing success.
            fwprintf(stderr, L"BackupComplete failed: 0x%08X\n", hr);
        }
        deleteSnapshot();
        cleanup();
        return finalizeFailed ? 4 : 0;
    }

    // Abort path: timeout, "abort", EOF, or wait error.
    abortWriters();
    deleteSnapshot();
    cleanup();
    return (signal == SignalResult::Timeout) ? 2 : 3;
}

}  // namespace

int wmain(int argc, wchar_t* argv[]) {
    std::wstring volume;
    DWORD timeoutSecs = 3600;  // 1 hour default

    for (int i = 1; i < argc; i++) {
        std::wstring arg = argv[i];
        if (arg == L"--volume" && i + 1 < argc) {
            volume = argv[++i];
        } else if (arg == L"--timeout" && i + 1 < argc) {
            timeoutSecs = static_cast<DWORD>(_wtoi(argv[++i]));
            if (timeoutSecs == 0) timeoutSecs = 3600;
        } else if (arg == L"--help" || arg == L"-h" || arg == L"/?") {
            PrintUsage();
            return 0;
        } else {
            fwprintf(stderr, L"Unknown argument: %ls\n", arg.c_str());
            PrintUsage();
            return 1;
        }
    }

    if (volume.empty()) {
        fwprintf(stderr, L"--volume is required\n");
        PrintUsage();
        return 1;
    }

    std::wstring normVolume = NormalizeVolume(volume);
    return Run(normVolume, timeoutSecs);
}
