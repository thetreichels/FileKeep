// FileKeepVss.exe — Minimal VSS shadow copy helper for FileKeep.
//
// Creates a VSS shadow copy of a volume, prints the snapshot device path
// to stdout, waits for a "done" signal on stdin (or timeout), then deletes
// the snapshot.
//
// Protocol:
//   FileKeepVss.exe --volume C: [--timeout 3600]
//   → stdout: \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy3\n
//   → stdin:  "done\n" or EOF → deletes snapshot, exits 0
//   → timeout → deletes snapshot, exits 2
//   → any VSS failure → exits 1 with error on stderr
//
// This is intentionally minimal: ~300 lines, no dependencies beyond the
// Windows SDK. The backup engine (managed) spawns this process and never
// touches COM itself.

#define _WIN32_DCOM
#include <windows.h>
#include <vss.h>
#include <vswriter.h>
#include <vsbackup.h>

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
        "waits for \"done\" on stdin (or timeout), then deletes the snapshot.\n"
        "\n"
        "  --volume   Volume to snapshot (e.g., C: or C:\\)\n"
        "  --timeout  Seconds to hold the snapshot (default: 3600)\n"
        "\n"
        "Exit codes:\n"
        "  0  Snapshot created, used, and deleted cleanly\n"
        "  1  VSS error (see stderr)\n"
        "  2  Timeout waiting for done signal\n");
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

    // Ensure cleanup on any exit path
    auto cleanup = [&]() {
        if (snapshotCreated && backup) {
            // Delete the snapshot. Use VSS_OBJECT_SNAPSHOT to delete
            // just this snapshot, not the whole set.
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
        }
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

    // Start a new snapshot set
    hr = backup->StartSnapshotSet(&snapshotSetId);
    if (FAILED(hr)) {
        fwprintf(stderr, L"StartSnapshotSet failed: 0x%08X\n", hr);
        cleanup();
        return 1;
    }

    // Add the volume to the set
    hr = backup->AddToSnapshotSet(
        const_cast<LPWSTR>(volume.c_str()),
        GUID_NULL,  // no specific provider
        &snapshotId);
    if (FAILED(hr)) {
        fwprintf(stderr, L"AddToSnapshotSet failed for %ls: 0x%08X\n",
            volume.c_str(), hr);
        cleanup();
        return 1;
    }

    // Prepare for backup (notifies writers)
    {
        IVssAsync* async = nullptr;
        hr = backup->PrepareForBackup(&async);
        if (SUCCEEDED(hr)) {
            hr = WaitForAsync(async);
            async->Release();
        }
        if (FAILED(hr)) {
            fwprintf(stderr, L"PrepareForBackup failed: 0x%08X\n", hr);
            cleanup();
            return 1;
        }
    }

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
        cleanup();
        return 1;
    }

    // Print the device path to stdout (the protocol)
    // Format: \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN
    wprintf(L"%ls\n", prop.m_pwszSnapshotDeviceObject);
    fflush(stdout);

    VssFreeSnapshotProperties(&prop);

    // Wait for "done" on stdin or timeout
    // Use WaitForSingleObject on stdin handle with timeout
    HANDLE hStdin = GetStdHandle(STD_INPUT_HANDLE);
    DWORD waitMs = timeoutSecs * 1000;

    // If stdin is not a console/pipe (e.g., redirected from NUL), we just
    // wait for the timeout. Check if input is available.
    DWORD waitResult = WaitForSingleObject(hStdin, waitMs);
    if (waitResult == WAIT_TIMEOUT) {
        fwprintf(stderr, L"Timeout (%lu seconds) waiting for done signal\n",
            timeoutSecs);
        cleanup();
        return 2;
    }

    // Input available (or handle signaled) — read it to confirm "done",
    // but accept EOF as well. Either way, we clean up.
    // (We don't strictly validate the content; the engine signals
    // completion by closing stdin or writing "done".)

    cleanup();
    return 0;
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
