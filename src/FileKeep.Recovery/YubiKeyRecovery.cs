using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FileKeep.Core.Security;

namespace FileKeep.Recovery;

/// <summary>
/// Raw-HID YubiKey challenge-response for the WinPE recovery app.
///
/// No Yubico SDK dependency: the SDK may not load in WinPE, so this talks
/// to the key directly over HID via P/Invoke (hid.dll / setupapi.dll).
/// WinPE must include the HID class drivers (hidclass.sys, hidparse.sys,
/// hidusb.sys) — verified by winre/build-winre-usb.ps1.
///
/// Protocol: YubiKey OTP application, HMAC-SHA1 challenge-response.
/// Device: VID 0x1050 (Yubico), vendor usage page 0xFF00.
/// Commands go as 8-byte HID feature reports; the challenge is written in
/// 7-byte payload chunks and the 20-byte HMAC-SHA1 response is read back.
///
/// NOTE: the slot command bytes below follow the ykpers protocol. They are
/// isolated here (not scattered through the wizard) so hardware validation
/// with a physical key can confirm or correct them in one place.
/// </summary>
public static class YubiKeyRecovery
{
    private const ushort YubicoVid = 0x1050;
    private const ushort VendorUsagePage = 0xFF00;

    // ykpers slot commands for HMAC-SHA1 challenge-response.
    // *** VERIFY ON HARDWARE — isolated here for the physical-key test. ***
    private const byte SlotChalHmac1 = 0x22;
    private const byte SlotChalHmac2 = 0x2A;

    private const int FeatureReportSize = 8; // 1 command byte + 7 payload bytes
    private const int HmacSha1Length = 20;

    public sealed record YubiKeyDeviceInfo(string Path, string Description);

    /// <summary>
    /// Enumerates HID YubiKeys via SetupDi. Returns the device paths of
    /// Yubico HID devices (VID 0x1050) on the vendor usage page.
    /// </summary>
    public static IReadOnlyList<YubiKeyDeviceInfo> DetectKeys()
    {
        var found = new List<YubiKeyDeviceInfo>();
        if (!OperatingSystem.IsWindows())
            return found;

        Guid hidGuid = Guid.Empty;
        HidD_GetHidGuid(ref hidGuid);

        nint devInfo = SetupDiGetClassDevs(ref hidGuid, null, nint.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devInfo == INVALID_HANDLE_VALUE)
            return found;

        try
        {
            var ifaceData = new SP_DEVICE_INTERFACE_DATA
            {
                cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>()
            };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(devInfo, nint.Zero,
                ref hidGuid, i, ref ifaceData); i++)
            {
                string? path = GetDevicePath(devInfo, ref ifaceData, ref hidGuid);
                if (path is null || !path.Contains("vid_1050",
                    StringComparison.OrdinalIgnoreCase))
                    continue;

                // Confirm it's the vendor-defined (OTP) interface, not the
                // keyboard interface, by checking the HID usage page.
                nint h = CreateFile(path, 0,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    nint.Zero, OPEN_EXISTING, 0, nint.Zero);
                if (h == INVALID_HANDLE_VALUE)
                    continue;
                try
                {
                    if (TryGetUsagePage(h, out ushort usagePage) &&
                        usagePage == VendorUsagePage)
                    {
                        found.Add(new YubiKeyDeviceInfo(path,
                            "YubiKey HID device"));
                    }
                }
                finally { CloseHandle(h); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devInfo); }

        return found;
    }

    /// <summary>
    /// Performs an HMAC-SHA1 challenge-response against the first detected
    /// YubiKey. Blocks until the user touches the key (if the slot requires
    /// touch) or <paramref name="timeout"/> elapses.
    /// Returns the 20-byte HMAC-SHA1 response (caller must wipe after use).
    /// </summary>
    public static byte[] ChallengeResponse(byte[] challenge, int slot,
        TimeSpan timeout)
    {
        if (challenge is null || challenge.Length == 0)
            throw new ArgumentException("Challenge must not be empty.", nameof(challenge));
        if (challenge.Length > 64)
            throw new ArgumentException("Challenge must be at most 64 bytes.", nameof(challenge));
        if (slot is not 1 and not 2)
            throw new ArgumentOutOfRangeException(nameof(slot), "Slot must be 1 or 2.");

        var keys = DetectKeys();
        if (keys.Count == 0)
            throw new InvalidOperationException("No YubiKey detected.");

        byte slotCmd = slot == 1 ? SlotChalHmac1 : SlotChalHmac2;

        nint h = CreateFile(keys[0].Path, GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            nint.Zero, OPEN_EXISTING, 0, nint.Zero);
        if (h == INVALID_HANDLE_VALUE)
            throw new InvalidOperationException("Could not open YubiKey HID device.");

        try
        {
            // Write the challenge as feature reports: first report carries
            // the slot command byte, then 7-byte payload chunks.
            WriteChallenge(h, slotCmd, challenge);

            // Poll for the response until timeout. The key holds the
            // response until the user touches (if touch is required).
            var deadline = DateTime.UtcNow + timeout;
            var response = new byte[HmacSha1Length];
            while (DateTime.UtcNow < deadline)
            {
                if (TryReadResponse(h, response))
                    return response;
                Thread.Sleep(100);
            }
            CryptographicOperations.ZeroMemory(response);
            throw new TimeoutException(
                $"No YubiKey touch within {timeout.TotalSeconds:F0} seconds.");
        }
        finally { CloseHandle(h); }
    }

    /// <summary>
    /// Derives the 32-byte data key from a YubiKey HMAC-SHA1 response.
    /// Must produce byte-identical output to
    /// <see cref="YubiKeyCrypto.DeriveKek"/> — enforced by unit tests
    /// against RFC 2202 vectors.
    /// </summary>
    public static byte[] DeriveKey(byte[] hmacResponse)
    {
        // Deliberately delegates to the canonical implementation so the
        // recovery path can never drift from the backup path.
        return YubiKeyCrypto.DeriveKek(hmacResponse);
    }

    // ---- HID framing (ykpers protocol) ----

    private static void WriteChallenge(nint h, byte slotCmd, byte[] challenge)
    {
        // First report: command byte + first up-to-7 challenge bytes.
        int offset = 0;
        var report = new byte[FeatureReportSize + 1]; // +1 for report ID
        report[0] = 0; // report ID
        report[1] = slotCmd;
        int first = Math.Min(7, challenge.Length);
        Buffer.BlockCopy(challenge, 0, report, 2, first);
        offset = first;
        if (!HidD_SetFeature(h, report, report.Length))
            throw new InvalidOperationException("Failed to send challenge to YubiKey.");

        // Continuation reports: 8 payload bytes each (report ID + 8).
        while (offset < challenge.Length)
        {
            Array.Clear(report);
            int n = Math.Min(8, challenge.Length - offset);
            Buffer.BlockCopy(challenge, offset, report, 1, n);
            offset += n;
            if (!HidD_SetFeature(h, report, report.Length))
                throw new InvalidOperationException("Failed to send challenge to YubiKey.");
        }
    }

    private static bool TryReadResponse(nint h, byte[] response)
    {
        var report = new byte[FeatureReportSize + 1];
        report[0] = 0;
        if (!HidD_GetFeature(h, report, report.Length))
            return false;

        // A pending (untouched) key returns all zeros; a completed
        // challenge-response returns the 20-byte HMAC starting at byte 1.
        // We need 3 reports (7 + 7 + 6) for the full 20 bytes; the key
        // presents them as sequential feature-report reads.
        int copied = 0;
        for (int r = 0; r < 3 && copied < HmacSha1Length; r++)
        {
            if (r > 0)
            {
                Array.Clear(report);
                report[0] = 0;
                if (!HidD_GetFeature(h, report, report.Length))
                    return false;
            }
            int n = Math.Min(7, HmacSha1Length - copied);
            // Skip report ID at [0]; payload is [1..8].
            bool allZero = true;
            for (int i = 1; i <= n; i++)
                if (report[i] != 0) { allZero = false; break; }
            if (allZero && r == 0)
                return false; // key hasn't produced a response yet
            Buffer.BlockCopy(report, 1, response, copied, n);
            copied += n;
        }
        return copied == HmacSha1Length;
    }

    // ---- SetupDi enumeration ----

    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_DEVICEINTERFACE = 0x00000010;
    private static readonly nint INVALID_HANDLE_VALUE = new(-1);

    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SP_DEVICE_INTERFACE_DETAIL_DATA
    {
        public int cbSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string DevicePath;
    }

    private static string? GetDevicePath(nint devInfo,
        ref SP_DEVICE_INTERFACE_DATA ifaceData, ref Guid hidGuid)
    {
        uint requiredSize = 0;
        SetupDiGetDeviceInterfaceDetail(devInfo, ref ifaceData, nint.Zero,
            0, ref requiredSize, nint.Zero);
        if (requiredSize == 0)
            return null;

        nint detailBuf = Marshal.AllocHGlobal((int)requiredSize);
        try
        {
            // cbSize is 8 on 64-bit, 6 on 32-bit for this struct.
            Marshal.WriteInt32(detailBuf, nint.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(devInfo, ref ifaceData,
                detailBuf, requiredSize, ref requiredSize, nint.Zero))
                return null;
            nint pathPtr = detailBuf + (nint.Size == 8 ? 8 : 4);
            return Marshal.PtrToStringUni(pathPtr);
        }
        finally { Marshal.FreeHGlobal(detailBuf); }
    }

    private static bool TryGetUsagePage(nint h, out ushort usagePage)
    {
        usagePage = 0;
        nint preparsed = nint.Zero;
        try
        {
            if (!HidD_GetPreparsedData(h, ref preparsed) || preparsed == nint.Zero)
                return false;
            var caps = new HIDP_CAPS();
            if (HidP_GetCaps(preparsed, ref caps) != 0) // HIDP_STATUS_SUCCESS
                return false;
            usagePage = caps.UsagePage;
            return true;
        }
        finally
        {
            if (preparsed != nint.Zero)
                HidD_FreePreparsedData(preparsed);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll", SetLastError = true)]
    private static extern void HidD_GetHidGuid(ref Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(nint hObject,
        ref nint preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(nint preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetFeature(nint hObject,
        byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(nint hObject,
        byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern int HidP_GetCaps(nint preparsedData,
        ref HIDP_CAPS capabilities);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(ref Guid classGuid,
        string? enumerator, nint hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(nint devInfo,
        nint devData, ref Guid interfaceClassGuid, uint memberIndex,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(nint devInfo,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        nint deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize,
        ref uint requiredSize, nint deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint devInfo);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateFile(string lpFileName,
        uint dwDesiredAccess, uint dwShareMode, nint lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);
}
