using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ADShield.Core;

/// <summary>
/// Creates, attaches, and detaches VHDX virtual disks via the native
/// Windows VirtDisk.dll API — no diskpart scripts, no external executables.
/// </summary>
public static class VhdxManager
{
    // ── P/Invoke Declarations ─────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct VIRTUAL_STORAGE_TYPE
    {
        public uint DeviceId;
        public Guid VendorId;

        public static readonly Guid VENDOR_MICROSOFT =
            new("EC984AEC-A0F9-47e9-901F-71415A66345B");
        public const uint DEVICE_VHDX = 3;
        public const uint DEVICE_VHD  = 2;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREATE_VIRTUAL_DISK_PARAMETERS
    {
        public uint   Version;               // 1
        public Guid   UniqueId;
        public ulong  MaximumSize;           // bytes
        public uint   BlockSizeInBytes;      // 0 = default
        public uint   SectorSizeInBytes;     // 512
        public IntPtr ParentLocationBuffer;  // null
        public IntPtr SourceLocationBuffer;  // null
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OPEN_VIRTUAL_DISK_PARAMETERS
    {
        public uint Version;    // 1
        public uint ReadOnly;   // BOOL
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ATTACH_VIRTUAL_DISK_PARAMETERS
    {
        public uint Version;   // 1
        public uint Reserved;
    }

    private enum VIRTUAL_DISK_ACCESS_MASK : uint
    {
        VIRTUAL_DISK_ACCESS_NONE      = 0x00000000,
        VIRTUAL_DISK_ACCESS_ALL       = 0x003f0000,
        VIRTUAL_DISK_ACCESS_WRITABLE  = 0x00220000,
    }

    private enum CREATE_VIRTUAL_DISK_FLAG : uint
    {
        CREATE_VIRTUAL_DISK_FLAG_NONE = 0,
    }

    private enum OPEN_VIRTUAL_DISK_FLAG : uint
    {
        OPEN_VIRTUAL_DISK_FLAG_NONE = 0,
    }

    private enum ATTACH_VIRTUAL_DISK_FLAG : uint
    {
        ATTACH_VIRTUAL_DISK_FLAG_NONE            = 0x00000000,
        ATTACH_VIRTUAL_DISK_FLAG_PERMANENT_LIFETIME = 0x00000004,
    }

    private enum DETACH_VIRTUAL_DISK_FLAG : uint
    {
        DETACH_VIRTUAL_DISK_FLAG_NONE = 0,
    }

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int CreateVirtualDisk(
        ref VIRTUAL_STORAGE_TYPE      VirtualStorageType,
        string                        Path,
        VIRTUAL_DISK_ACCESS_MASK      VirtualDiskAccessMask,
        IntPtr                        SecurityDescriptor,
        CREATE_VIRTUAL_DISK_FLAG      Flags,
        uint                          ProviderSpecificFlags,
        ref CREATE_VIRTUAL_DISK_PARAMETERS Parameters,
        IntPtr                        Overlapped,
        out SafeFileHandle            Handle);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int OpenVirtualDisk(
        ref VIRTUAL_STORAGE_TYPE      VirtualStorageType,
        string                        Path,
        VIRTUAL_DISK_ACCESS_MASK      VirtualDiskAccessMask,
        OPEN_VIRTUAL_DISK_FLAG        Flags,
        ref OPEN_VIRTUAL_DISK_PARAMETERS Parameters,
        out SafeFileHandle            Handle);

    [DllImport("virtdisk.dll", SetLastError = true)]
    private static extern int AttachVirtualDisk(
        SafeFileHandle                VirtualDiskHandle,
        IntPtr                        SecurityDescriptor,
        ATTACH_VIRTUAL_DISK_FLAG      Flags,
        uint                          ProviderSpecificFlags,
        ref ATTACH_VIRTUAL_DISK_PARAMETERS Parameters,
        IntPtr                        Overlapped);

    [DllImport("virtdisk.dll", SetLastError = true)]
    private static extern int DetachVirtualDisk(
        SafeFileHandle                VirtualDiskHandle,
        DETACH_VIRTUAL_DISK_FLAG      Flags,
        uint                          ProviderSpecificFlags);

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new dynamically expanding VHDX at the given path.
    /// Default size is 1 TB (1,099,511,627,776 bytes).
    /// </summary>
    public static void CreateVhdx(string vhdxPath, ulong sizeBytes = 1_099_511_627_776UL,
        IProgress<string>? progress = null)
    {
        if (File.Exists(vhdxPath))
        {
            progress?.Report($"[INFO] VHDX already exists at {vhdxPath}.");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(vhdxPath)!);
        var sizeGbDisplay = sizeBytes / (1024.0 * 1024 * 1024);
        progress?.Report($"[INFO] Creating {sizeGbDisplay:F0} GB VHDX at {vhdxPath}...");

        var storageType = new VIRTUAL_STORAGE_TYPE
        {
            DeviceId = VIRTUAL_STORAGE_TYPE.DEVICE_VHDX,
            VendorId = VIRTUAL_STORAGE_TYPE.VENDOR_MICROSOFT
        };

        var parameters = new CREATE_VIRTUAL_DISK_PARAMETERS
        {
            Version             = 1,
            UniqueId            = Guid.NewGuid(),
            MaximumSize         = sizeBytes,
            BlockSizeInBytes    = 0,        // default
            SectorSizeInBytes   = 512,
            ParentLocationBuffer = IntPtr.Zero,
            SourceLocationBuffer = IntPtr.Zero
        };

        int hr = CreateVirtualDisk(
            ref storageType, vhdxPath,
            VIRTUAL_DISK_ACCESS_MASK.VIRTUAL_DISK_ACCESS_ALL,
            IntPtr.Zero,
            CREATE_VIRTUAL_DISK_FLAG.CREATE_VIRTUAL_DISK_FLAG_NONE,
            0, ref parameters, IntPtr.Zero, out var handle);

        handle?.Dispose();

        if (hr != 0)
            throw new Exception($"CreateVirtualDisk failed. HRESULT: 0x{hr:X8} ({Marshal.GetLastWin32Error()})");

        progress?.Report("[SUCCESS] VHDX container created.");
    }

    /// <summary>Attaches (mounts) an existing VHDX as a local disk.</summary>
    public static void AttachVhdx(string vhdxPath, IProgress<string>? progress = null)
    {
        progress?.Report($"[INFO] Attaching VHDX: {vhdxPath}");

        var storageType = new VIRTUAL_STORAGE_TYPE
        {
            DeviceId = VIRTUAL_STORAGE_TYPE.DEVICE_VHDX,
            VendorId = VIRTUAL_STORAGE_TYPE.VENDOR_MICROSOFT
        };
        var openParams = new OPEN_VIRTUAL_DISK_PARAMETERS { Version = 1, ReadOnly = 0 };

        int hr = OpenVirtualDisk(
            ref storageType, vhdxPath,
            VIRTUAL_DISK_ACCESS_MASK.VIRTUAL_DISK_ACCESS_ALL,
            OPEN_VIRTUAL_DISK_FLAG.OPEN_VIRTUAL_DISK_FLAG_NONE,
            ref openParams, out var handle);

        if (hr != 0)
            throw new Exception($"OpenVirtualDisk failed. HRESULT: 0x{hr:X8}");

        using (handle)
        {
            var attachParams = new ATTACH_VIRTUAL_DISK_PARAMETERS { Version = 1, Reserved = 0 };
            hr = AttachVirtualDisk(
                handle, IntPtr.Zero,
                ATTACH_VIRTUAL_DISK_FLAG.ATTACH_VIRTUAL_DISK_FLAG_PERMANENT_LIFETIME,
                0, ref attachParams, IntPtr.Zero);

            if (hr != 0)
                throw new Exception($"AttachVirtualDisk failed. HRESULT: 0x{hr:X8}");
        }

        progress?.Report("[SUCCESS] VHDX attached.");
    }

    /// <summary>Detaches (unmounts) a VHDX.</summary>
    public static void DetachVhdx(string vhdxPath, IProgress<string>? progress = null)
    {
        progress?.Report($"[INFO] Detaching VHDX: {vhdxPath}");

        var storageType = new VIRTUAL_STORAGE_TYPE
        {
            DeviceId = VIRTUAL_STORAGE_TYPE.DEVICE_VHDX,
            VendorId = VIRTUAL_STORAGE_TYPE.VENDOR_MICROSOFT
        };
        var openParams = new OPEN_VIRTUAL_DISK_PARAMETERS { Version = 1, ReadOnly = 0 };

        int hr = OpenVirtualDisk(
            ref storageType, vhdxPath,
            VIRTUAL_DISK_ACCESS_MASK.VIRTUAL_DISK_ACCESS_ALL,
            OPEN_VIRTUAL_DISK_FLAG.OPEN_VIRTUAL_DISK_FLAG_NONE,
            ref openParams, out var handle);

        if (hr != 0)
            throw new Exception($"OpenVirtualDisk (for detach) failed. HRESULT: 0x{hr:X8}");

        using (handle)
        {
            hr = DetachVirtualDisk(handle, DETACH_VIRTUAL_DISK_FLAG.DETACH_VIRTUAL_DISK_FLAG_NONE, 0);
            if (hr != 0)
                throw new Exception($"DetachVirtualDisk failed. HRESULT: 0x{hr:X8}");
        }

        progress?.Report("[SUCCESS] VHDX detached.");
    }

    public static bool VhdxExists(string path) => File.Exists(path);
}
