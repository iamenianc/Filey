using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using OpenMcdf;

namespace Filey
{
    /// <summary>
    /// Raised when a workbook cannot be decrypted. <see cref="WrongPassword"/> distinguishes an
    /// incorrect password (a user-recoverable condition) from structural/format problems.
    /// </summary>
    public sealed class ExcelDecryptException : Exception
    {
        public bool WrongPassword { get; }

        public ExcelDecryptException(string message, bool wrongPassword = false) : base(message)
        {
            WrongPassword = wrongPassword;
        }
    }

    /// <summary>
    /// Removes the open (encryption) password from an OOXML workbook (.xlsx/.xlsm) entirely in
    /// managed code - no Excel, COM, or PowerShell. Implements ECMA-376 / [MS-OFFCRYPTO] Agile
    /// Encryption: the encrypted workbook is an OLE2 compound file whose <c>EncryptedPackage</c>
    /// stream decrypts to the original .xlsx ZIP byte-for-byte, so macros, VBA, and formatting are
    /// preserved. Only Agile encryption is supported (the default for Excel 2010+).
    /// </summary>
    public static class ExcelDecryptor
    {
        // OLE2/Compound File Binary header magic and the ZIP local-file-header magic.
        private static readonly byte[] Ole2Magic = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
        private static readonly byte[] ZipMagic = { 0x50, 0x4B, 0x03, 0x04 };

        // Fixed block keys from [MS-OFFCRYPTO] used to salt the per-purpose key derivations.
        private static readonly byte[] BlockVerifierHashInput = { 0xfe, 0xa7, 0xd2, 0x76, 0x3b, 0x4b, 0x9e, 0x79 };
        private static readonly byte[] BlockVerifierHashValue = { 0xd7, 0xaa, 0x0f, 0x6d, 0x30, 0x61, 0x34, 0x4e };
        private static readonly byte[] BlockKeyValue          = { 0x14, 0x6e, 0x0b, 0xe7, 0xab, 0xac, 0xd0, 0xd6 };

        private const int PackageSegmentSize = 4096;

        /// <summary>
        /// Decrypts <paramref name="inputPath"/> using <paramref name="password"/> and returns the
        /// raw, password-free .xlsx bytes. Throws <see cref="ExcelDecryptException"/> for wrong
        /// passwords, unencrypted files, and unsupported/corrupt formats.
        /// </summary>
        public static byte[] Decrypt(string inputPath, string password)
        {
            byte[] head = ReadHead(inputPath, 8);

            if (StartsWith(head, ZipMagic))
                throw new ExcelDecryptException("This file is not password-protected, so there is nothing to remove.");

            if (!StartsWith(head, Ole2Magic))
                throw new ExcelDecryptException("Unrecognized file format - not an encrypted Office workbook.");

            byte[] encryptionInfo;
            byte[] encryptedPackage;
            try
            {
                using (var cf = RootStorage.OpenRead(inputPath))
                {
                    encryptionInfo = TryGetStream(cf, "EncryptionInfo");
                    encryptedPackage = TryGetStream(cf, "EncryptedPackage");
                }
            }
            catch (ExcelDecryptException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ExcelDecryptException($"The file could not be read as an Office compound file: {ex.Message}");
            }

            if (encryptionInfo == null || encryptedPackage == null)
            {
                throw new ExcelDecryptException(
                    "This file does not use Office open-password encryption (legacy .xls encryption is not supported).");
            }

            // EncryptionInfo header: VersionMajor(2) VersionMinor(2) Flags(4), then the UTF-8 XML.
            if (encryptionInfo.Length < 8)
                throw new ExcelDecryptException("The EncryptionInfo stream is malformed.");

            int versionMajor = encryptionInfo[0] | (encryptionInfo[1] << 8);
            int versionMinor = encryptionInfo[2] | (encryptionInfo[3] << 8);
            if (!(versionMajor == 4 && versionMinor == 4))
            {
                throw new ExcelDecryptException(
                    "Only Agile encryption is supported; this file uses a different (Standard or legacy) scheme.");
            }

            AgileInfo info = ParseAgileInfo(encryptionInfo);

            // Verify the password up front so we can report a clean "incorrect password" instead of
            // producing garbage output.
            VerifyPassword(info, password);

            // Recover the intermediate key that actually encrypts the package, then decrypt it.
            byte[] passwordKey = DeriveKey(password, info.EncKeySalt, info.SpinCount, BlockKeyValue,
                info.EncKeyBits / 8, info.EncKeyHash);
            byte[] secretKey = AesCbcDecrypt(info.EncryptedKeyValue, passwordKey, FitBlock(info.EncKeySalt, info.EncKeyBlockSize));
            secretKey = Take(secretKey, info.KeyDataKeyBits / 8);

            return DecryptPackage(encryptedPackage, secretKey, info);
        }

        // ---- Agile metadata ------------------------------------------------------------------

        private sealed class AgileInfo
        {
            // keyData (used to decrypt the package itself)
            public byte[] KeyDataSalt;
            public int KeyDataBlockSize;
            public int KeyDataKeyBits;
            public string KeyDataHash;

            // password keyEncryptor (used to unwrap the secret key + verify the password)
            public int SpinCount;
            public byte[] EncKeySalt;
            public int EncKeyBlockSize;
            public int EncKeyBits;
            public string EncKeyHash;
            public byte[] EncryptedVerifierHashInput;
            public byte[] EncryptedVerifierHashValue;
            public byte[] EncryptedKeyValue;
        }

        private static AgileInfo ParseAgileInfo(byte[] encryptionInfo)
        {
            string xml = Encoding.UTF8.GetString(encryptionInfo, 8, encryptionInfo.Length - 8);
            XDocument doc;
            try { doc = XDocument.Parse(xml); }
            catch (Exception ex) { throw new ExcelDecryptException($"The encryption descriptor is not valid XML: {ex.Message}"); }

            XNamespace e = "http://schemas.microsoft.com/office/2006/encryption";
            XNamespace p = "http://schemas.microsoft.com/office/2006/keyEncryptor/password";

            XElement keyData = doc.Root?.Element(e + "keyData");
            XElement encKey = doc.Root?.Element(e + "keyEncryptors")?
                .Elements(e + "keyEncryptor")
                .Select(k => k.Element(p + "encryptedKey"))
                .FirstOrDefault(k => k != null);

            if (keyData == null || encKey == null)
                throw new ExcelDecryptException("The encryption descriptor is missing required elements.");

            string chaining = (string)encKey.Attribute("cipherChaining") ?? "ChainingModeCBC";
            if (!string.Equals(chaining, "ChainingModeCBC", StringComparison.OrdinalIgnoreCase))
                throw new ExcelDecryptException($"Unsupported cipher chaining mode: {chaining}.");

            return new AgileInfo
            {
                KeyDataSalt      = Convert.FromBase64String((string)keyData.Attribute("saltValue")),
                KeyDataBlockSize = (int)keyData.Attribute("blockSize"),
                KeyDataKeyBits   = (int)keyData.Attribute("keyBits"),
                KeyDataHash      = (string)keyData.Attribute("hashAlgorithm"),

                SpinCount        = (int)encKey.Attribute("spinCount"),
                EncKeySalt       = Convert.FromBase64String((string)encKey.Attribute("saltValue")),
                EncKeyBlockSize  = (int)encKey.Attribute("blockSize"),
                EncKeyBits       = (int)encKey.Attribute("keyBits"),
                EncKeyHash       = (string)encKey.Attribute("hashAlgorithm"),
                EncryptedVerifierHashInput = Convert.FromBase64String((string)encKey.Attribute("encryptedVerifierHashInput")),
                EncryptedVerifierHashValue = Convert.FromBase64String((string)encKey.Attribute("encryptedVerifierHashValue")),
                EncryptedKeyValue          = Convert.FromBase64String((string)encKey.Attribute("encryptedKeyValue")),
            };
        }

        private static void VerifyPassword(AgileInfo info, string password)
        {
            byte[] iv = FitBlock(info.EncKeySalt, info.EncKeyBlockSize);

            byte[] verifierInput = AesCbcDecrypt(info.EncryptedVerifierHashInput,
                DeriveKey(password, info.EncKeySalt, info.SpinCount, BlockVerifierHashInput, info.EncKeyBits / 8, info.EncKeyHash), iv);
            byte[] verifierHash = AesCbcDecrypt(info.EncryptedVerifierHashValue,
                DeriveKey(password, info.EncKeySalt, info.SpinCount, BlockVerifierHashValue, info.EncKeyBits / 8, info.EncKeyHash), iv);

            byte[] computed;
            using (HashAlgorithm h = CreateHash(info.EncKeyHash))
                computed = h.ComputeHash(verifierInput);

            // verifierHash is padded up to the cipher block size; compare only the real hash length.
            int hashLen = computed.Length;
            if (verifierHash.Length < hashLen || !ConstantTimeEquals(computed, verifierHash, hashLen))
                throw new ExcelDecryptException("Incorrect password.", wrongPassword: true);
        }

        private static byte[] DecryptPackage(byte[] encryptedPackage, byte[] secretKey, AgileInfo info)
        {
            if (encryptedPackage.Length < 8)
                throw new ExcelDecryptException("The EncryptedPackage stream is malformed.");

            long totalSize = BitConverter.ToInt64(encryptedPackage, 0);
            int cipherLen = encryptedPackage.Length - 8;
            if (cipherLen % info.KeyDataBlockSize != 0)
                throw new ExcelDecryptException("The encrypted package is corrupt (unaligned length).");

            var output = new byte[cipherLen];
            int written = 0;

            using (HashAlgorithm h = CreateHash(info.KeyDataHash))
            using (Aes aes = CreateAes(secretKey))
            {
                for (int segment = 0, pos = 8; pos < encryptedPackage.Length; segment++, pos += PackageSegmentSize)
                {
                    int chunk = Math.Min(PackageSegmentSize, encryptedPackage.Length - pos);

                    // Each 4096-byte segment is an independent CBC block chain keyed by a per-segment
                    // IV = Hash(keyDataSalt || segmentIndexLE) truncated to the block size.
                    aes.IV = FitBlock(h.ComputeHash(Concat(info.KeyDataSalt, LE(segment))), info.KeyDataBlockSize);
                    using (ICryptoTransform dec = aes.CreateDecryptor())
                    {
                        byte[] plain = dec.TransformFinalBlock(encryptedPackage, pos, chunk);
                        Buffer.BlockCopy(plain, 0, output, written, plain.Length);
                        written += plain.Length;
                    }
                }
            }

            if (totalSize < 0 || totalSize > written)
                throw new ExcelDecryptException("The decrypted package size is invalid (wrong password or corrupt file).");

            byte[] result = Take(output, (int)totalSize);
            if (!StartsWith(result, ZipMagic))
                throw new ExcelDecryptException("Decryption did not produce a valid workbook (wrong password or corrupt file).");
            return result;
        }

        // ---- Key derivation & crypto primitives ---------------------------------------------

        /// <summary>
        /// [MS-OFFCRYPTO] password key derivation: H0 = Hash(salt || UTF16LE(pwd)); Hn =
        /// Hash(iteratorLE || H(n-1)) for spinCount rounds; final = Hash(Hn || blockKey), fit to keyLen.
        /// </summary>
        private static byte[] DeriveKey(string password, byte[] salt, int spinCount, byte[] blockKey, int keyLen, string hashAlg)
        {
            using (HashAlgorithm h = CreateHash(hashAlg))
            {
                byte[] pwd = Encoding.Unicode.GetBytes(password); // UTF-16LE
                byte[] hash = h.ComputeHash(Concat(salt, pwd));
                for (uint i = 0; i < spinCount; i++)
                    hash = h.ComputeHash(Concat(LE(i), hash));
                hash = h.ComputeHash(Concat(hash, blockKey));
                return FitKey(hash, keyLen);
            }
        }

        private static Aes CreateAes(byte[] key)
        {
            Aes aes = Aes.Create();
            aes.KeySize = key.Length * 8;
            aes.Key = key;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            return aes;
        }

        private static byte[] AesCbcDecrypt(byte[] data, byte[] key, byte[] iv)
        {
            using (Aes aes = CreateAes(key))
            {
                aes.IV = iv;
                using (ICryptoTransform dec = aes.CreateDecryptor())
                    return dec.TransformFinalBlock(data, 0, data.Length);
            }
        }

        private static HashAlgorithm CreateHash(string alg)
        {
            switch ((alg ?? string.Empty).Replace("-", string.Empty).ToUpperInvariant())
            {
                case "SHA512": return SHA512.Create();
                case "SHA384": return SHA384.Create();
                case "SHA256": return SHA256.Create();
                case "SHA1":   return SHA1.Create();
                default: throw new ExcelDecryptException($"Unsupported hash algorithm: {alg}.");
            }
        }

        // ---- Small helpers -------------------------------------------------------------------

        // Truncate a hash/salt to an AES key length, zero-padding with 0x36 per the spec if short.
        private static byte[] FitKey(byte[] src, int size) => Fit(src, size, 0x36);
        // Truncate a hash/salt to a cipher block size (IV), zero-padding with 0x36 if short.
        private static byte[] FitBlock(byte[] src, int size) => Fit(src, size, 0x36);

        private static byte[] Fit(byte[] src, int size, byte pad)
        {
            var dst = new byte[size];
            if (src.Length >= size)
            {
                Array.Copy(src, dst, size);
            }
            else
            {
                Array.Copy(src, dst, src.Length);
                for (int i = src.Length; i < size; i++) dst[i] = pad;
            }
            return dst;
        }

        private static byte[] Take(byte[] src, int count)
        {
            var dst = new byte[count];
            Array.Copy(src, dst, count);
            return dst;
        }

        private static byte[] LE(uint value) => BitConverter.GetBytes(value); // app targets little-endian Windows
        private static byte[] LE(int value) => BitConverter.GetBytes(value);

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, r, 0, a.Length);
            Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
            return r;
        }

        private static bool StartsWith(byte[] data, byte[] prefix)
        {
            if (data.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++)
                if (data[i] != prefix[i]) return false;
            return true;
        }

        private static bool ConstantTimeEquals(byte[] a, byte[] b, int length)
        {
            int diff = 0;
            for (int i = 0; i < length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static byte[] ReadHead(string path, int count)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                var buf = new byte[count];
                int read = fs.Read(buf, 0, count);
                return read == count ? buf : Take(buf, read);
            }
        }

        private static byte[] TryGetStream(RootStorage cf, string name)
        {
            if (!cf.TryOpenStream(name, out CfbStream stream)) return null;
            using (stream)
            using (var mem = new MemoryStream())
            {
                stream.CopyTo(mem);
                return mem.ToArray();
            }
        }
    }
}
