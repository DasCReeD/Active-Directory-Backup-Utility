using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ADShield.Core;

/// <summary>
/// DPAPI-backed secret storage for the unattended VeraCrypt vault passphrase.
/// Uses native Windows crypt32.dll P/Invoke (CryptProtectData / CryptUnprotectData)
/// so no external NuGet packages are required.
/// </summary>
public static class VaultSecretStore
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    private static readonly string VaultFilePath = Path.Combine(AppConfig.DataDir, "vault.dat");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>Encrypts and saves the VeraCrypt passphrase using Windows DPAPI.</summary>
    public static void SavePassphrase(string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Passphrase cannot be empty.", nameof(password));

        Directory.CreateDirectory(AppConfig.DataDir);

        byte[] plainBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            byte[] protectedBytes = Protect(plainBytes);
            File.WriteAllBytes(VaultFilePath, protectedBytes);
        }
        finally
        {
            Array.Clear(plainBytes, 0, plainBytes.Length);
        }
    }

    /// <summary>Loads and decrypts the stored VeraCrypt passphrase, or returns null if not set or unreadable.</summary>
    public static string? LoadPassphrase()
    {
        try
        {
            if (!File.Exists(VaultFilePath)) return null;

            byte[] encryptedBytes = File.ReadAllBytes(VaultFilePath);
            if (encryptedBytes.Length == 0) return null;

            byte[]? plainBytes = Unprotect(encryptedBytes);
            if (plainBytes is null || plainBytes.Length == 0) return null;

            try
            {
                return Encoding.UTF8.GetString(plainBytes);
            }
            finally
            {
                Array.Clear(plainBytes, 0, plainBytes.Length);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Deletes the stored encrypted passphrase file if it exists.</summary>
    public static void ClearPassphrase()
    {
        if (File.Exists(VaultFilePath))
            File.Delete(VaultFilePath);
    }

    /// <summary>Returns true if a non-empty encrypted passphrase file exists.</summary>
    public static bool HasPassphrase()
    {
        return File.Exists(VaultFilePath) && new FileInfo(VaultFilePath).Length > 0;
    }

    private static byte[] Protect(byte[] plainBytes)
    {
        var inBlob = new DATA_BLOB
        {
            cbData = plainBytes.Length,
            pbData = Marshal.AllocHGlobal(plainBytes.Length),
        };
        var outBlob = default(DATA_BLOB);

        try
        {
            Marshal.Copy(plainBytes, 0, inBlob.pbData, plainBytes.Length);

            if (!CryptProtectData(
                ref inBlob,
                "ADShield VeraCrypt Vault",
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                CRYPTPROTECT_UI_FORBIDDEN,
                out outBlob))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            byte[] result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            ZeroAndFreeHGlobal(inBlob.pbData, inBlob.cbData);
            if (outBlob.pbData != IntPtr.Zero)
                LocalFree(outBlob.pbData);
        }
    }

    private static byte[]? Unprotect(byte[] encryptedBytes)
    {
        var inBlob = new DATA_BLOB
        {
            cbData = encryptedBytes.Length,
            pbData = Marshal.AllocHGlobal(encryptedBytes.Length),
        };
        var outBlob = default(DATA_BLOB);

        try
        {
            Marshal.Copy(encryptedBytes, 0, inBlob.pbData, encryptedBytes.Length);

            if (!CryptUnprotectData(
                ref inBlob,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                CRYPTPROTECT_UI_FORBIDDEN,
                out outBlob))
            {
                return null;
            }

            byte[] result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero)
            {
                ZeroMemory(outBlob.pbData, outBlob.cbData);
                LocalFree(outBlob.pbData);
            }
        }
    }

    private static void ZeroMemory(IntPtr ptr, int length)
    {
        if (ptr == IntPtr.Zero || length <= 0) return;
        for (int i = 0; i < length; i++)
            Marshal.WriteByte(ptr, i, 0);
    }

    private static void ZeroAndFreeHGlobal(IntPtr ptr, int length)
    {
        if (ptr == IntPtr.Zero) return;
        ZeroMemory(ptr, length);
        Marshal.FreeHGlobal(ptr);
    }
}
