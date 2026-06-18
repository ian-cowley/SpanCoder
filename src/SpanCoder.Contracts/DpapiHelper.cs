using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SpanCoder.Contracts
{
    public static class DpapiHelper
    {
        #region Windows DPAPI P/Invoke

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
            ref DATA_BLOB pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            int dwFlags,
            ref DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(
            ref DATA_BLOB pDataIn,
            IntPtr ppszDataDescr,
            ref DATA_BLOB pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            int dwFlags,
            ref DATA_BLOB pDataOut);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);

        #endregion

        private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        public static string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return "";

            if (IsWindows)
            {
                try
                {
                    byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
                    DATA_BLOB dataIn = new DATA_BLOB { cbData = plainBytes.Length, pbData = Marshal.AllocHGlobal(plainBytes.Length) };
                    Marshal.Copy(plainBytes, 0, dataIn.pbData, plainBytes.Length);

                    DATA_BLOB dataOut = new DATA_BLOB();
                    DATA_BLOB entropy = new DATA_BLOB();

                    try
                    {
                        if (CryptProtectData(ref dataIn, "SpanCoderKey", ref entropy, IntPtr.Zero, IntPtr.Zero, 0, ref dataOut))
                        {
                            byte[] cipherBytes = new byte[dataOut.cbData];
                            Marshal.Copy(dataOut.pbData, cipherBytes, 0, dataOut.cbData);
                            return Convert.ToBase64String(cipherBytes);
                        }
                    }
                    finally
                    {
                        if (dataIn.pbData != IntPtr.Zero) Marshal.FreeHGlobal(dataIn.pbData);
                        if (dataOut.pbData != IntPtr.Zero) LocalFree(dataOut.pbData);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DpapiHelper] Windows encryption failed: {ex.Message}");
                }
            }

            // Fallback for non-Windows (Linux/WSL/macOS) using AES
            return EncryptFallback(plainText);
        }

        public static string Decrypt(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText)) return "";

            if (IsWindows)
            {
                try
                {
                    byte[] cipherBytes = Convert.FromBase64String(cipherText);
                    DATA_BLOB dataIn = new DATA_BLOB { cbData = cipherBytes.Length, pbData = Marshal.AllocHGlobal(cipherBytes.Length) };
                    Marshal.Copy(cipherBytes, 0, dataIn.pbData, cipherBytes.Length);

                    DATA_BLOB dataOut = new DATA_BLOB();
                    DATA_BLOB entropy = new DATA_BLOB();

                    try
                    {
                        if (CryptUnprotectData(ref dataIn, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 0, ref dataOut))
                        {
                            byte[] plainBytes = new byte[dataOut.cbData];
                            Marshal.Copy(dataOut.pbData, plainBytes, 0, dataOut.cbData);
                            return Encoding.UTF8.GetString(plainBytes);
                        }
                    }
                    finally
                    {
                        if (dataIn.pbData != IntPtr.Zero) Marshal.FreeHGlobal(dataIn.pbData);
                        if (dataOut.pbData != IntPtr.Zero) LocalFree(dataOut.pbData);
                    }
                }
                catch
                {
                    // If it was encrypted with fallback, or decryption failed, attempt fallback decrypt
                }
            }

            return DecryptFallback(cipherText);
        }

        #region Cross-platform AES Fallback

        private static byte[] GetFallbackKey()
        {
            string machine = Environment.MachineName;
            string user = Environment.UserName;
            string keyStr = (machine + user + "SpanCoderFallbackSalt!").PadRight(32).Substring(0, 32);
            return Encoding.UTF8.GetBytes(keyStr);
        }

        private static byte[] GetFallbackIv()
        {
            return Encoding.UTF8.GetBytes("SpanCoderIV12345"); // 16 bytes
        }

        private static string EncryptFallback(string plainText)
        {
            try
            {
                using var aes = Aes.Create();
                aes.Key = GetFallbackKey();
                aes.IV = GetFallbackIv();

                using var ms = new MemoryStream();
                using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                {
                    byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
                    cs.Write(plainBytes, 0, plainBytes.Length);
                }
                return Convert.ToBase64String(ms.ToArray());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DpapiHelper] Fallback encryption failed: {ex.Message}");
                return "";
            }
        }

        private static string DecryptFallback(string cipherText)
        {
            try
            {
                byte[] cipherBytes = Convert.FromBase64String(cipherText);
                using var aes = Aes.Create();
                aes.Key = GetFallbackKey();
                aes.IV = GetFallbackIv();

                using var ms = new MemoryStream();
                using (var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
                {
                    cs.Write(cipherBytes, 0, cipherBytes.Length);
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
            catch
            {
                return "";
            }
        }

        #endregion
    }
}
