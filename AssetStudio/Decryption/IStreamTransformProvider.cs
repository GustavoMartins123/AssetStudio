#nullable enable
using K4os.Compression.LZ4;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace AssetStudio
{
    public interface IStreamTransformProvider
    {
        string Id { get; }
        int Priority { get; }
        Stream? TryTransform(Stream stream, string filePath, ReadOnlySpan<byte> initialHeader);
    }

    public sealed class DecryptionProfile
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Cipher algorithm: "xor", "aes-cbc", "aes-ecb", "aes-ctr", "chacha20", "block512", or "none" (header skip only).
        /// When omitted, defaults to "xor" if xorKeyHex/keyHex/keyUtf8 is provided, else "none".
        /// </summary>
        [JsonPropertyName("algorithm")]
        public string? Algorithm { get; set; }

        /// <summary>
        /// Wildcard filename matcher. Multiple patterns may be separated with ';' or ','.
        /// </summary>
        [JsonPropertyName("fileNamePattern")]
        public string? FileNamePattern { get; set; }

        /// <summary>
        /// Optional wildcard matcher evaluated against the full normalized path (forward slashes).
        /// </summary>
        [JsonPropertyName("pathPattern")]
        public string? PathPattern { get; set; }

        [JsonPropertyName("magicHex")]
        public string? MagicHex { get; set; }

        [JsonPropertyName("magicOffset")]
        public int MagicOffset { get; set; }

        [JsonPropertyName("minFileSize")]
        public long MinFileSize { get; set; }

        [JsonPropertyName("sizeMultiple")]
        public int SizeMultiple { get; set; }

        [JsonPropertyName("skipBytes")]
        public long SkipBytes { get; set; }

        [JsonPropertyName("xorKeyHex")]
        public string? XorKeyHex { get; set; }

        [JsonPropertyName("keyHex")]
        public string? KeyHex { get; set; }

        [JsonPropertyName("keyUtf8")]
        public string? KeyUtf8 { get; set; }

        [JsonPropertyName("ivHex")]
        public string? IvHex { get; set; }

        [JsonPropertyName("nonceHex")]
        public string? NonceHex { get; set; }

        [JsonPropertyName("ivUtf8")]
        public string? IvUtf8 { get; set; }

        /// <summary>
        /// When true, reads the IV/nonce (16 bytes for AES-CBC/CTR, 12 bytes for ChaCha20)
        /// from the stream immediately after skipBytes, and advances the payload start past it.
        /// </summary>
        [JsonPropertyName("ivFromStreamPrefix")]
        public bool IvFromStreamPrefix { get; set; }

        /// <summary>
        /// Optional per-file IV derivation from filename: "md5" or "sha256".
        /// </summary>
        [JsonPropertyName("ivFromFileNameHash")]
        public string? IvFromFileNameHash { get; set; }

        /// <summary>
        /// When true, uses the target file's name (or stem when useFileNameWithoutExtension is true)
        /// as the KDF salt (or appends it to the key material before SHA256/MD5 hashing).
        /// </summary>
        [JsonPropertyName("useFileNameAsSalt")]
        public bool UseFileNameAsSalt { get; set; }

        /// <summary>
        /// When true, per-file filename derivation uses Path.GetFileNameWithoutExtension(filePath).
        /// </summary>
        [JsonPropertyName("useFileNameWithoutExtension")]
        public bool UseFileNameWithoutExtension { get; set; }

        /// <summary>
        /// Block cipher padding: "none" (default) or "pkcs7".
        /// </summary>
        [JsonPropertyName("padding")]
        public string? Padding { get; set; }

        [JsonPropertyName("counterStart")]
        public uint CounterStart { get; set; }

        [JsonPropertyName("counterLittleEndian")]
        public bool CounterLittleEndian { get; set; }

        /// <summary>
        /// Optional key derivation: "none" (default), "sha256", "md5", "pbkdf2-sha1", or "pbkdf2-sha256".
        /// </summary>
        [JsonPropertyName("keyDerivation")]
        public string? KeyDerivation { get; set; }

        [JsonPropertyName("saltHex")]
        public string? SaltHex { get; set; }

        [JsonPropertyName("saltUtf8")]
        public string? SaltUtf8 { get; set; }

        [JsonPropertyName("kdfIterations")]
        public int KdfIterations { get; set; }

        [JsonPropertyName("derivedKeyLength")]
        public int DerivedKeyLength { get; set; }

        [JsonPropertyName("rangeStart")]
        public long RangeStart { get; set; }

        [JsonPropertyName("rangeLength")]
        public long RangeLength { get; set; } = -1;

        /// <summary>
        /// Optional explicit Addressables catalog path (catalog.json / catalog_*.json / catalog.bin)
        /// used by "block512" profiles when auto-discovery cannot find the catalog adjacent to the bundle.
        /// </summary>
        [JsonPropertyName("catalogPath")]
        public string? CatalogPath { get; set; }

        /// <summary>
        /// Optional direct Block 512 token (e.g. "bundleName_hash-crc") used by "block512" profiles
        /// when no Addressables catalog is available.
        /// </summary>
        [JsonPropertyName("block512Token")]
        public string? Block512Token { get; set; }

        /// <summary>
        /// When true, skips already-plaintext Unity files and verifies that the transformed stream
        /// begins with a recognizable Unity bundle, SerializedFile, WebFile, or compressed container header.
        /// </summary>
        [JsonPropertyName("validateUnityHeader")]
        public bool ValidateUnityHeader { get; set; }

        public DecryptionProfile Clone()
        {
            return new DecryptionProfile
            {
                Id = Id,
                Description = Description,
                Enabled = Enabled,
                Algorithm = Algorithm,
                FileNamePattern = FileNamePattern,
                PathPattern = PathPattern,
                MagicHex = MagicHex,
                MagicOffset = MagicOffset,
                MinFileSize = MinFileSize,
                SizeMultiple = SizeMultiple,
                SkipBytes = SkipBytes,
                XorKeyHex = XorKeyHex,
                KeyHex = KeyHex,
                KeyUtf8 = KeyUtf8,
                IvHex = IvHex,
                NonceHex = NonceHex,
                IvUtf8 = IvUtf8,
                IvFromStreamPrefix = IvFromStreamPrefix,
                IvFromFileNameHash = IvFromFileNameHash,
                UseFileNameAsSalt = UseFileNameAsSalt,
                UseFileNameWithoutExtension = UseFileNameWithoutExtension,
                Padding = Padding,
                CounterStart = CounterStart,
                CounterLittleEndian = CounterLittleEndian,
                KeyDerivation = KeyDerivation,
                SaltHex = SaltHex,
                SaltUtf8 = SaltUtf8,
                KdfIterations = KdfIterations,
                DerivedKeyLength = DerivedKeyLength,
                RangeStart = RangeStart,
                RangeLength = RangeLength,
                CatalogPath = CatalogPath,
                Block512Token = Block512Token,
                ValidateUnityHeader = ValidateUnityHeader
            };
        }

        public StreamCipherAlgorithm ResolveAlgorithm()
        {
            var raw = (Algorithm ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(raw))
            {
                if (!string.IsNullOrWhiteSpace(CatalogPath) || !string.IsNullOrWhiteSpace(Block512Token))
                {
                    return StreamCipherAlgorithm.Block512;
                }
                if (!string.IsNullOrWhiteSpace(XorKeyHex)
                    || !string.IsNullOrWhiteSpace(KeyHex)
                    || !string.IsNullOrEmpty(KeyUtf8))
                {
                    return StreamCipherAlgorithm.Xor;
                }
                return StreamCipherAlgorithm.None;
            }

            return raw switch
            {
                "xor" => StreamCipherAlgorithm.Xor,
                "aes" or "aes-cbc" or "aescbc" or "aes_cbc" => StreamCipherAlgorithm.AesCbc,
                "aes-ecb" or "aesecb" or "aes_ecb" => StreamCipherAlgorithm.AesEcb,
                "aes-ctr" or "aesctr" or "aes_ctr" => StreamCipherAlgorithm.AesCtr,
                "chacha20" or "chacha" or "chacha-20" => StreamCipherAlgorithm.ChaCha20,
                "block512" or "block-512" or "block_512" => StreamCipherAlgorithm.Block512,
                "none" or "skip" or "offset" => StreamCipherAlgorithm.None,
                _ => throw new ArgumentException($"Unsupported decryption algorithm '{Algorithm}'.")
            };
        }

        public bool Validate(out string? error)
        {
            error = null;
            StreamCipherAlgorithm algo;
            try
            {
                algo = ResolveAlgorithm();
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            if (SkipBytes < 0)
            {
                error = "Skip bytes cannot be negative.";
                return false;
            }

            if (RangeStart < 0)
            {
                error = "Range start cannot be negative.";
                return false;
            }

            if (algo == StreamCipherAlgorithm.None)
            {
                if (SkipBytes <= 0)
                {
                    error = "Offset-skip profile requires SkipBytes > 0.";
                    return false;
                }
                return true;
            }

            if (algo == StreamCipherAlgorithm.Block512)
            {
                bool hasToken = !string.IsNullOrWhiteSpace(Block512Token) || !string.IsNullOrEmpty(KeyUtf8);
                bool hasCatalog = !string.IsNullOrWhiteSpace(CatalogPath);
                if (!hasToken && !hasCatalog)
                {
                    error = "Block 512 profile requires an Addressables catalog path or a direct Block 512 token.";
                    return false;
                }

                if (hasCatalog && !File.Exists(CatalogPath!.Trim()))
                {
                    error = $"Block 512 catalog file not found: '{CatalogPath}'.";
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(MagicHex) && DeclarativeProfileTransformProvider.TryParseHexBytes(MagicHex) == null)
                {
                    error = "Magic hex contains invalid hexadecimal characters.";
                    return false;
                }

                return true;
            }

            byte[]? keyBytes = DeclarativeProfileTransformProvider.TryResolveKeyBytes(this, algo, out string? keyError);
            if (keyBytes == null || keyBytes.Length == 0)
            {
                error = keyError ?? "A valid decryption key is required.";
                return false;
            }

            switch (algo)
            {
                case StreamCipherAlgorithm.AesCbc:
                case StreamCipherAlgorithm.AesEcb:
                case StreamCipherAlgorithm.AesCtr:
                    if (keyBytes.Length is not (16 or 24 or 32))
                    {
                        error = $"AES key must be 16, 24, or 32 bytes (got {keyBytes.Length} bytes).";
                        return false;
                    }
                    break;
                case StreamCipherAlgorithm.ChaCha20:
                    if (keyBytes.Length != 32)
                    {
                        error = $"ChaCha20 key must be 32 bytes (got {keyBytes.Length} bytes).";
                        return false;
                    }
                    break;
            }

            if (!IvFromStreamPrefix)
            {
                if (!DeclarativeProfileTransformProvider.TryParseExplicitIvOrNonce(this, algo, out _, out string? ivError))
                {
                    error = ivError ?? "Invalid IV/nonce format.";
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(MagicHex) && DeclarativeProfileTransformProvider.TryParseHexBytes(MagicHex) == null)
            {
                error = "Magic hex contains invalid hexadecimal characters.";
                return false;
            }

            return true;
        }
    }

    public sealed class DeclarativeProfileTransformProvider : IStreamTransformProvider
    {
        private readonly Func<IReadOnlyList<DecryptionProfile>> getProfiles;

        public DeclarativeProfileTransformProvider(Func<IReadOnlyList<DecryptionProfile>> getProfiles)
        {
            this.getProfiles = getProfiles ?? throw new ArgumentNullException(nameof(getProfiles));
        }

        public string Id => "DeclarativeProfile";
        public int Priority => 200;

        public Stream? TryTransform(Stream stream, string filePath, ReadOnlySpan<byte> initialHeader)
        {
            var profiles = getProfiles();
            if (profiles.Count == 0)
            {
                return null;
            }

            bool alreadyPlaintextUnity = UnityHeaderProbe.TryParseUnityBundleHeader(initialHeader, 0, stream.Length, out _, out _, out _, out _)
                || UnityHeaderProbe.IsSerializedFileHeader(initialHeader, stream.Length);

            string fileName = Path.GetFileName(filePath);
            string normalizedPath = (filePath ?? string.Empty).Replace('\\', '/');
            long originalPos = stream.Position;

            for (int i = 0; i < profiles.Count; i++)
            {
                var profile = profiles[i];
                if (!profile.Enabled)
                {
                    continue;
                }

                if (profile.ValidateUnityHeader && alreadyPlaintextUnity)
                {
                    continue;
                }

                if (!MatchesProfile(profile, fileName, normalizedPath, initialHeader, stream.Length))
                {
                    continue;
                }

                stream.Position = originalPos;
                var candidate = TryCreateTransformedStream(profile, stream, filePath, fileName, originalPos);
                if (candidate == null)
                {
                    stream.Position = originalPos;
                    continue;
                }

                if (!profile.ValidateUnityHeader)
                {
                    return candidate;
                }

                if (ValidateDecryptedUnityHeader(candidate, out _))
                {
                    if (candidate is SubStream sub && profile.SkipBytes > 0 && profile.SkipBytes < stream.Length)
                    {
                        sub.Dispose();
                        var finalSub = new SubStream(stream, profile.SkipBytes, stream.Length - profile.SkipBytes, leaveOpen: false);
                        finalSub.Position = 0;
                        return finalSub;
                    }

                    SetLeaveBaseStreamOpen(candidate, leaveOpen: false);
                    candidate.Position = 0;
                    return candidate;
                }

                SetLeaveBaseStreamOpen(candidate, leaveOpen: true);
                candidate.Dispose();
                stream.Position = originalPos;
            }

            return null;
        }

        internal static bool TryTestProfileOnStream(
            DecryptionProfile profile,
            Stream stream,
            string filePath,
            out string? headerSummary)
        {
            headerSummary = null;
            if (profile == null || stream == null || !stream.CanRead || !stream.CanSeek)
            {
                return false;
            }

            long originalPos = stream.Position;
            var testProfile = profile.Clone();
            testProfile.Enabled = true;
            testProfile.ValidateUnityHeader = true;

            string fileName = Path.GetFileName(filePath);
            var candidate = TryCreateTransformedStream(testProfile, stream, filePath, fileName, originalPos);
            if (candidate == null)
            {
                stream.Position = originalPos;
                return false;
            }

            try
            {
                SetLeaveBaseStreamOpen(candidate, leaveOpen: true);
                return ValidateDecryptedUnityHeader(candidate, out headerSummary);
            }
            finally
            {
                SetLeaveBaseStreamOpen(candidate, leaveOpen: true);
                candidate.Dispose();
                stream.Position = originalPos;
            }
        }

        private static Stream? TryCreateTransformedStream(
            DecryptionProfile profile,
            Stream stream,
            string? filePath,
            string fileName,
            long originalPos)
        {
            StreamCipherAlgorithm algo;
            try
            {
                algo = profile.ResolveAlgorithm();
            }
            catch
            {
                return null;
            }

            if (algo == StreamCipherAlgorithm.Block512)
            {
                if (stream.Length < 26)
                {
                    return null;
                }

                string? token = !string.IsNullOrWhiteSpace(profile.Block512Token)
                    ? profile.Block512Token!.Trim()
                    : (!string.IsNullOrEmpty(profile.KeyUtf8)
                        ? profile.KeyUtf8
                        : DecryptionHelper.TryResolveBlock512Token(filePath, profile.CatalogPath));

                if (string.IsNullOrWhiteSpace(token))
                {
                    return null;
                }

                var decrypted = new DecryptedStream(stream, token)
                {
                    LeaveBaseStreamOpen = profile.ValidateUnityHeader
                };
                return decrypted;
            }

            long skipBytes = Math.Max(0, profile.SkipBytes);
            if (skipBytes >= stream.Length)
            {
                return null;
            }

            if (algo == StreamCipherAlgorithm.None)
            {
                if (skipBytes > 0)
                {
                    return new SubStream(stream, skipBytes, stream.Length - skipBytes, leaveOpen: profile.ValidateUnityHeader);
                }
                return null;
            }

            string fileToken = ResolveFileToken(profile, fileName);
            byte[]? keyBytes = TryResolveKeyBytes(profile, algo, out _, fileToken);
            if (keyBytes == null || keyBytes.Length == 0)
            {
                return null;
            }

            if (algo == StreamCipherAlgorithm.Xor)
            {
                var xorStream = new XorTransformStream(
                    stream,
                    keyBytes,
                    headerSkipBytes: skipBytes,
                    rangeStart: Math.Max(0, profile.RangeStart),
                    rangeLength: profile.RangeLength);
                if (profile.ValidateUnityHeader)
                {
                    xorStream.LeaveBaseStreamOpen = true;
                }
                return xorStream;
            }

            byte[]? ivOrNonce = null;
            long effectiveSkip = skipBytes;

            if (profile.IvFromStreamPrefix && algo is StreamCipherAlgorithm.AesCbc or StreamCipherAlgorithm.AesCtr or StreamCipherAlgorithm.ChaCha20)
            {
                int ivLen = algo == StreamCipherAlgorithm.ChaCha20 ? 12 : 16;
                if (skipBytes + ivLen >= stream.Length)
                {
                    return null;
                }

                ivOrNonce = new byte[ivLen];
                stream.Position = skipBytes;
                int read = 0;
                while (read < ivLen)
                {
                    int n = stream.Read(ivOrNonce, read, ivLen - read);
                    if (n <= 0) break;
                    read += n;
                }
                stream.Position = originalPos;
                if (read < ivLen)
                {
                    return null;
                }

                effectiveSkip += ivLen;
            }
            else if (!string.IsNullOrWhiteSpace(profile.IvFromFileNameHash))
            {
                ivOrNonce = DeriveIvFromFileToken(profile.IvFromFileNameHash!, fileToken, algo);
                if (ivOrNonce == null)
                {
                    return null;
                }
            }
            else
            {
                if (!TryParseExplicitIvOrNonce(profile, algo, out ivOrNonce, out _))
                {
                    return null;
                }
            }

            bool pkcs7 = string.Equals(profile.Padding?.Trim(), "pkcs7", StringComparison.OrdinalIgnoreCase);

            try
            {
                var cipherStream = new CipherTransformStream(
                    stream,
                    algo,
                    keyBytes,
                    ivOrNonce,
                    headerSkipBytes: effectiveSkip,
                    rangeStart: Math.Max(0, profile.RangeStart),
                    rangeLength: profile.RangeLength,
                    pkcs7Padding: pkcs7,
                    counterStart: profile.CounterStart,
                    counterLittleEndian: profile.CounterLittleEndian);
                if (profile.ValidateUnityHeader)
                {
                    cipherStream.LeaveBaseStreamOpen = true;
                }
                return cipherStream;
            }
            catch
            {
                return null;
            }
        }

        internal static bool ValidateDecryptedUnityHeader(Stream candidate, out string? headerSummary)
        {
            headerSummary = null;
            try
            {
                if (candidate.Length < 20)
                {
                    return false;
                }

                candidate.Position = 0;
                int probeLen = (int)Math.Min(256, candidate.Length);
                Span<byte> buf = stackalloc byte[probeLen];
                int read = 0;
                while (read < probeLen)
                {
                    int n = candidate.Read(buf.Slice(read));
                    if (n <= 0) break;
                    read += n;
                }

                if (read < 20)
                {
                    return false;
                }

                var span = buf.Slice(0, read);
                if (UnityHeaderProbe.TryParseUnityBundleHeader(span, 0, candidate.Length, out var bundleSig, out _, out var unityVersion, out _, requireStrictValidation: true))
                {
                    headerSummary = string.IsNullOrWhiteSpace(unityVersion) ? bundleSig : $"{bundleSig} ({unityVersion})";
                    return true;
                }

                if (UnityHeaderProbe.IsSerializedFileHeader(span, candidate.Length))
                {
                    headerSummary = "Unity SerializedFile";
                    return true;
                }

                string sig = UnityHeaderProbe.ReadNullTerminatedAscii(span, 0, 20, out _);
                if (string.Equals(sig, "UnityWebData1.0", StringComparison.Ordinal))
                {
                    headerSummary = "UnityWebData1.0";
                    return true;
                }

                // GZip magic (1F 8B) or Brotli marker at 0x20
                if (span[0] == 0x1F && span[1] == 0x8B)
                {
                    headerSummary = "GZip container";
                    return true;
                }
                if (span.Length >= 0x26
                    && span[0x20] == 'b' && span[0x21] == 'r' && span[0x22] == 'o'
                    && span[0x23] == 't' && span[0x24] == 'l' && span[0x25] == 'i')
                {
                    headerSummary = "Brotli container";
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static void SetLeaveBaseStreamOpen(Stream stream, bool leaveOpen)
        {
            if (stream is CipherTransformStream cipher)
            {
                cipher.LeaveBaseStreamOpen = leaveOpen;
            }
            else if (stream is XorTransformStream xor)
            {
                xor.LeaveBaseStreamOpen = leaveOpen;
            }
            else if (stream is DecryptedStream block512)
            {
                block512.LeaveBaseStreamOpen = leaveOpen;
            }
        }

        private static string ResolveFileToken(DecryptionProfile profile, string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return string.Empty;
            }

            return profile.UseFileNameWithoutExtension
                ? Path.GetFileNameWithoutExtension(fileName)
                : fileName;
        }

        private static byte[]? DeriveIvFromFileToken(string hashKind, string fileToken, StreamCipherAlgorithm algo)
        {
            byte[] tokenBytes = Encoding.UTF8.GetBytes(fileToken ?? string.Empty);
            int targetLen = algo == StreamCipherAlgorithm.ChaCha20 ? 12 : 16;
            string mode = hashKind.Trim().ToLowerInvariant();
            byte[] hash = mode switch
            {
                "md5" => MD5.HashData(tokenBytes),
                "sha256" or "sha-256" => SHA256.HashData(tokenBytes),
                _ => Array.Empty<byte>()
            };

            if (hash.Length < targetLen)
            {
                return null;
            }

            return hash.AsSpan(0, targetLen).ToArray();
        }

        internal static byte[]? TryResolveKeyBytes(DecryptionProfile profile, StreamCipherAlgorithm algo, out string? error, string? fileToken = null)
        {
            error = null;
            byte[]? rawKey = null;

            if (!string.IsNullOrWhiteSpace(profile.KeyHex))
            {
                rawKey = TryParseHexBytes(profile.KeyHex);
                if (rawKey == null)
                {
                    error = "Key (Hex) is not a valid even-length hexadecimal string.";
                    return null;
                }
            }
            else if (!string.IsNullOrEmpty(profile.KeyUtf8))
            {
                rawKey = Encoding.UTF8.GetBytes(profile.KeyUtf8);
            }
            else if (!string.IsNullOrWhiteSpace(profile.XorKeyHex))
            {
                rawKey = TryParseHexBytes(profile.XorKeyHex);
                if (rawKey == null)
                {
                    error = "XOR Key (Hex) is not a valid even-length hexadecimal string.";
                    return null;
                }
            }

            if (rawKey == null || rawKey.Length == 0)
            {
                error = "Key is required.";
                return null;
            }

            var kdf = (profile.KeyDerivation ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(kdf) || kdf == "none")
            {
                return rawKey;
            }

            int targetLen = profile.DerivedKeyLength > 0
                ? profile.DerivedKeyLength
                : algo switch
                {
                    StreamCipherAlgorithm.ChaCha20 => 32,
                    StreamCipherAlgorithm.AesCbc or StreamCipherAlgorithm.AesEcb or StreamCipherAlgorithm.AesCtr => 32,
                    _ => 32
                };

            byte[] effectiveInput = rawKey;
            if (profile.UseFileNameAsSalt && !string.IsNullOrEmpty(fileToken) && (kdf is "sha256" or "sha-256" or "md5"))
            {
                byte[] tokenBytes = Encoding.UTF8.GetBytes(fileToken);
                effectiveInput = new byte[rawKey.Length + tokenBytes.Length];
                Buffer.BlockCopy(rawKey, 0, effectiveInput, 0, rawKey.Length);
                Buffer.BlockCopy(tokenBytes, 0, effectiveInput, rawKey.Length, tokenBytes.Length);
            }

            switch (kdf)
            {
                case "sha256":
                case "sha-256":
                {
                    byte[] hash = SHA256.HashData(effectiveInput);
                    if (targetLen > 0 && targetLen < hash.Length)
                    {
                        return hash.AsSpan(0, targetLen).ToArray();
                    }
                    return hash;
                }
                case "md5":
                {
                    byte[] hash = MD5.HashData(effectiveInput);
                    if (targetLen > 0 && targetLen < hash.Length)
                    {
                        return hash.AsSpan(0, targetLen).ToArray();
                    }
                    return hash;
                }
                case "pbkdf2":
                case "pbkdf2-sha1":
                case "pbkdf2-sha256":
                {
                    byte[] salt = Array.Empty<byte>();
                    if (profile.UseFileNameAsSalt && !string.IsNullOrEmpty(fileToken))
                    {
                        salt = Encoding.UTF8.GetBytes(fileToken);
                    }
                    else if (!string.IsNullOrWhiteSpace(profile.SaltHex))
                    {
                        byte[]? parsedSalt = TryParseHexBytes(profile.SaltHex);
                        if (parsedSalt == null)
                        {
                            error = "KDF Salt (Hex) is not a valid hexadecimal string.";
                            return null;
                        }
                        salt = parsedSalt;
                    }
                    else if (!string.IsNullOrEmpty(profile.SaltUtf8))
                    {
                        salt = Encoding.UTF8.GetBytes(profile.SaltUtf8);
                    }

                    int iterations = profile.KdfIterations > 0 ? profile.KdfIterations : 1000;
                    var hashAlg = kdf == "pbkdf2-sha1" ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256;
                    return Rfc2898DeriveBytes.Pbkdf2(rawKey, salt, iterations, hashAlg, targetLen);
                }
                default:
                    error = $"Unsupported key derivation '{profile.KeyDerivation}'.";
                    return null;
            }
        }

        internal static bool TryParseExplicitIvOrNonce(
            DecryptionProfile profile,
            StreamCipherAlgorithm algo,
            out byte[]? ivOrNonce,
            out string? error)
        {
            ivOrNonce = null;
            error = null;

            string? hexSource = !string.IsNullOrWhiteSpace(profile.IvHex)
                ? profile.IvHex
                : (!string.IsNullOrWhiteSpace(profile.NonceHex) ? profile.NonceHex : null);

            if (!string.IsNullOrWhiteSpace(hexSource))
            {
                ivOrNonce = TryParseHexBytes(hexSource);
                if (ivOrNonce == null)
                {
                    error = "IV/Nonce (Hex) is not a valid even-length hexadecimal string.";
                    return false;
                }
            }
            else if (!string.IsNullOrEmpty(profile.IvUtf8))
            {
                ivOrNonce = Encoding.UTF8.GetBytes(profile.IvUtf8);
            }

            if (ivOrNonce == null || ivOrNonce.Length == 0)
            {
                return true; // Defaults to all-zero IV/nonce when omitted
            }

            switch (algo)
            {
                case StreamCipherAlgorithm.AesCbc:
                    if (ivOrNonce.Length != 16)
                    {
                        error = $"AES-CBC IV must be 16 bytes (got {ivOrNonce.Length} bytes).";
                        return false;
                    }
                    break;
                case StreamCipherAlgorithm.AesCtr:
                    if (ivOrNonce.Length is not (12 or 16))
                    {
                        error = $"AES-CTR IV/nonce must be 12 or 16 bytes (got {ivOrNonce.Length} bytes).";
                        return false;
                    }
                    break;
                case StreamCipherAlgorithm.ChaCha20:
                    if (ivOrNonce.Length != 12)
                    {
                        error = $"ChaCha20 nonce must be 12 bytes (got {ivOrNonce.Length} bytes).";
                        return false;
                    }
                    break;
            }

            return true;
        }

        private static bool MatchesProfile(
            DecryptionProfile profile,
            string fileName,
            string normalizedPath,
            ReadOnlySpan<byte> initialHeader,
            long streamLength)
        {
            if (profile.SkipBytes < 0 || profile.SkipBytes >= streamLength)
            {
                return false;
            }

            if (profile.MinFileSize > 0 && streamLength < profile.MinFileSize)
            {
                return false;
            }

            if (profile.SizeMultiple > 1 && ((streamLength - profile.SkipBytes) % profile.SizeMultiple) != 0)
            {
                return false;
            }

            bool hasMatcher = profile.ValidateUnityHeader;

            if (!string.IsNullOrWhiteSpace(profile.FileNamePattern))
            {
                hasMatcher = true;
                if (!MatchesAnyWildcard(fileName, profile.FileNamePattern!))
                {
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(profile.PathPattern))
            {
                hasMatcher = true;
                if (!MatchesAnyWildcard(normalizedPath, profile.PathPattern!))
                {
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(profile.MagicHex))
            {
                hasMatcher = true;
                byte[]? magic = TryParseHexBytes(profile.MagicHex);
                if (magic == null || magic.Length == 0 || !UnityHeaderProbe.MatchesAt(initialHeader, profile.MagicOffset, magic))
                {
                    return false;
                }
            }

            return hasMatcher;
        }

        private static bool MatchesAnyWildcard(string text, string patternList)
        {
            var parts = patternList.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i].Trim();
                if (p.Length > 0 && FileSystemName.MatchesSimpleExpression(p, text, ignoreCase: true))
                {
                    return true;
                }
            }
            return false;
        }

        internal static byte[]? TryParseHexBytes(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
            {
                return null;
            }

            string trimmed = hex.Trim();
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring(2);
            }

            Span<char> cleaned = trimmed.Length <= 512 ? stackalloc char[trimmed.Length] : new char[trimmed.Length];
            int len = 0;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (!char.IsWhiteSpace(c) && c != '-' && c != ':')
                {
                    cleaned[len++] = c;
                }
            }

            if (len == 0 || (len & 1) != 0)
            {
                return null;
            }

            try
            {
                return Convert.FromHexString(cleaned.Slice(0, len));
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }

    public sealed class OffsetHeaderSkipTransformProvider : IStreamTransformProvider
    {
        public string Id => "OffsetHeaderSkip";
        public int Priority => 300;

        public Stream? TryTransform(Stream stream, string filePath, ReadOnlySpan<byte> initialHeader)
        {
            if (initialHeader.Length < 32)
            {
                return null;
            }

            // Do not skip if offset 0 is already a valid Unity bundle or SerializedFile
            if (UnityHeaderProbe.TryParseUnityBundleHeader(initialHeader, 0, stream.Length, out _, out _, out _, out _)
                || UnityHeaderProbe.IsSerializedFileHeader(initialHeader, stream.Length))
            {
                return null;
            }

            if (!UnityHeaderProbe.TryFindOffsetUnityBundleHeader(initialHeader, stream.Length, out int offset, out _, out _, out _)
                || offset <= 0
                || offset >= stream.Length)
            {
                return null;
            }

            return new SubStream(stream, offset, stream.Length - offset, leaveOpen: false);
        }
    }

    public sealed class KnownPlaintextXorTransformProvider : IStreamTransformProvider
    {
        private static readonly long[] GenericPrefixCutoffs =
        {
            128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536, 102400, 131072, 262144
        };

        public string Id => "KnownPlaintextXor";
        public int Priority => 400;

        public Stream? TryTransform(Stream stream, string filePath, ReadOnlySpan<byte> initialHeader)
        {
            if (initialHeader.Length < 32)
            {
                return null;
            }

            if (UnityHeaderProbe.TryParseUnityBundleHeader(initialHeader, 0, stream.Length, out _, out _, out _, out _)
                || UnityHeaderProbe.IsSerializedFileHeader(initialHeader, stream.Length))
            {
                return null;
            }

            if (!UnityHeaderProbe.TryDetectRepeatingXorUnityFsKey(initialHeader, stream.Length, out byte[] xorKey, out _, out _))
            {
                return null;
            }

            long rangeLength = DetectXorRangeLength(stream, xorKey);
            stream.Position = 0;
            return new XorTransformStream(stream, xorKey, rangeLength: rangeLength);
        }

        private static long DetectXorRangeLength(Stream stream, byte[] xorKey)
        {
            if (!stream.CanSeek || stream.Length < 64)
            {
                return -1;
            }

            // Read enough decoded header bytes to parse the UnityFS header fields
            int headerBufLen = (int)Math.Min(stream.Length, 256);
            byte[] headerBuf = new byte[headerBufLen];
            stream.Position = 0;
            int read = ReadExactOrAvailable(stream, headerBuf, 0, headerBufLen);
            if (read < 48)
            {
                return -1;
            }

            UnityHeaderProbe.XorBytesRepeating(headerBuf.AsSpan(0, read), 0, xorKey);
            if (!TryParseUnityFsLayout(headerBuf.AsSpan(0, read), stream.Length, out var layout))
            {
                return -1;
            }

            // Fast path: check full-stream XOR first
            if (ValidateBundleBlocksUnderRange(stream, xorKey, -1, layout))
            {
                return -1;
            }

            // Probe generic prefix-only XOR cutoffs (common header/prefix encryption bounds)
            for (int i = 0; i < GenericPrefixCutoffs.Length; i++)
            {
                long cutoff = GenericPrefixCutoffs[i];
                if (cutoff >= stream.Length)
                {
                    break;
                }

                if (ValidateBundleBlocksUnderRange(stream, xorKey, cutoff, layout))
                {
                    return cutoff;
                }
            }

            return -1;
        }

        private readonly struct UnityFsLayout
        {
            public readonly uint Version;
            public readonly long BlocksInfoOffset;
            public readonly int CompressedBlocksInfoSize;
            public readonly int UncompressedBlocksInfoSize;
            public readonly ArchiveFlags Flags;
            public readonly long DataStartOffset;

            public UnityFsLayout(
                uint version,
                long blocksInfoOffset,
                int compressedBlocksInfoSize,
                int uncompressedBlocksInfoSize,
                ArchiveFlags flags,
                long dataStartOffset)
            {
                Version = version;
                BlocksInfoOffset = blocksInfoOffset;
                CompressedBlocksInfoSize = compressedBlocksInfoSize;
                UncompressedBlocksInfoSize = uncompressedBlocksInfoSize;
                Flags = flags;
                DataStartOffset = dataStartOffset;
            }
        }

        private static bool TryParseUnityFsLayout(ReadOnlySpan<byte> decodedHeader, long streamLength, out UnityFsLayout layout)
        {
            layout = default;
            int pos = 0;
            // Skip signature ("UnityFS\0")
            while (pos < decodedHeader.Length && decodedHeader[pos] != 0) pos++;
            if (pos >= decodedHeader.Length) return false;
            pos++;

            if (pos + 4 > decodedHeader.Length) return false;
            uint version = ReadUInt32BE(decodedHeader, pos);
            pos += 4;

            // Skip unityVersion and unityRevision null-terminated strings
            for (int s = 0; s < 2; s++)
            {
                while (pos < decodedHeader.Length && decodedHeader[pos] != 0) pos++;
                if (pos >= decodedHeader.Length) return false;
                pos++;
            }

            if (pos + 20 > decodedHeader.Length) return false;
            long size = ReadInt64BE(decodedHeader, pos); pos += 8;
            uint compBlocksInfo = ReadUInt32BE(decodedHeader, pos); pos += 4;
            uint uncompBlocksInfo = ReadUInt32BE(decodedHeader, pos); pos += 4;
            ArchiveFlags flags = (ArchiveFlags)ReadUInt32BE(decodedHeader, pos); pos += 4;

            if (size <= 0
                || compBlocksInfo == 0
                || uncompBlocksInfo == 0
                || compBlocksInfo > 16 * 1024 * 1024
                || uncompBlocksInfo > 64 * 1024 * 1024
                || compBlocksInfo > streamLength)
            {
                return false;
            }

            if (version >= 7)
            {
                pos = (pos + 15) & ~15;
            }

            bool atEnd = (flags & ArchiveFlags.BlocksInfoAtTheEnd) != 0;
            long blocksInfoOffset = atEnd ? (streamLength - compBlocksInfo) : pos;
            if (blocksInfoOffset < 0 || blocksInfoOffset + compBlocksInfo > streamLength)
            {
                return false;
            }

            long dataStart = atEnd ? pos : (pos + compBlocksInfo);
            if ((flags & ArchiveFlags.BlockInfoNeedPaddingAtStart) != 0)
            {
                dataStart = (dataStart + 15) & ~15;
            }

            if (dataStart < 0 || dataStart > streamLength)
            {
                return false;
            }

            layout = new UnityFsLayout(version, blocksInfoOffset, (int)compBlocksInfo, (int)uncompBlocksInfo, flags, dataStart);
            return true;
        }

        private static bool ValidateBundleBlocksUnderRange(
            Stream stream,
            byte[] xorKey,
            long rangeLength,
            UnityFsLayout layout)
        {
            byte[] compInfo = new byte[layout.CompressedBlocksInfoSize];
            if (!ReadRangeDecoded(stream, layout.BlocksInfoOffset, compInfo, xorKey, rangeLength))
            {
                return false;
            }

            byte[] uncompInfo;
            var infoComp = (CompressionType)(layout.Flags & ArchiveFlags.CompressionTypeMask);
            switch (infoComp)
            {
                case CompressionType.None:
                    if (compInfo.Length != layout.UncompressedBlocksInfoSize) return false;
                    uncompInfo = compInfo;
                    break;
                case CompressionType.Lz4:
                case CompressionType.Lz4HC:
                    uncompInfo = new byte[layout.UncompressedBlocksInfoSize];
                    try
                    {
                        int written = LZ4Codec.Decode(compInfo, uncompInfo);
                        if (written != layout.UncompressedBlocksInfoSize)
                        {
                            return false;
                        }
                    }
                    catch
                    {
                        return false;
                    }
                    break;
                default:
                    // For LZMA or other codecs, avoid expensive probing if we cannot cheaply validate blocksInfo
                    return rangeLength < 0;
            }

            // Parse blocksInfo: 16-byte hash + int32 blockCount + blockCount * (uint32 uncomp, uint32 comp, uint16 flags)
            if (uncompInfo.Length < 20)
            {
                return false;
            }

            int blockCount = (int)ReadUInt32BE(uncompInfo, 16);
            if (blockCount <= 0 || blockCount > 1_000_000 || 20L + blockCount * 10L > uncompInfo.Length)
            {
                return false;
            }

            // Validate directory count sanity after blocks table and read first directory node for SerializedFile verification
            int dirOffset = 20 + blockCount * 10;
            if (dirOffset + 4 > uncompInfo.Length)
            {
                return false;
            }
            int dirCount = (int)ReadUInt32BE(uncompInfo, dirOffset);
            if (dirCount <= 0 || dirCount > 100_000)
            {
                return false;
            }

            bool hasFirstSerializedNode = false;
            long firstNodeSize = 0;
            if (dirOffset + 4 + 20 <= uncompInfo.Length)
            {
                long firstNodeOffset = ReadInt64BE(uncompInfo, dirOffset + 4);
                long candidateNodeSize = ReadInt64BE(uncompInfo, dirOffset + 12);
                uint firstNodeFlags = ReadUInt32BE(uncompInfo, dirOffset + 20);
                if ((firstNodeFlags & 4u) != 0 && firstNodeOffset == 0 && candidateNodeSize >= 48)
                {
                    hasFirstSerializedNode = true;
                    firstNodeSize = candidateNodeSize;
                }
            }

            // Verify total compressed block payload fits within stream and probe first, straddling, and last LZ4 block
            long cursor = layout.DataStartOffset;
            int firstIdx = 0;
            int straddleIdx = -1;
            int lastIdx = blockCount - 1;
            long firstOffset = 0, straddleOffset = 0, lastOffset = 0;
            uint firstComp = 0, firstUncomp = 0, firstFlags = 0;
            uint straddleComp = 0, straddleUncomp = 0, straddleFlags = 0;
            uint lastComp = 0, lastUncomp = 0, lastFlags = 0;

            for (int i = 0; i < blockCount; i++)
            {
                int entryPos = 20 + i * 10;
                uint bUncomp = ReadUInt32BE(uncompInfo, entryPos);
                uint bComp = ReadUInt32BE(uncompInfo, entryPos + 4);
                uint bFlags = ReadUInt16BE(uncompInfo, entryPos + 8);

                if (bComp == 0 || bUncomp == 0 || cursor + bComp > stream.Length)
                {
                    return false;
                }

                if (i == firstIdx)
                {
                    firstOffset = cursor; firstComp = bComp; firstUncomp = bUncomp; firstFlags = bFlags;
                }
                if (rangeLength > 0 && straddleIdx < 0 && cursor < rangeLength && cursor + bComp > rangeLength)
                {
                    straddleIdx = i;
                    straddleOffset = cursor; straddleComp = bComp; straddleUncomp = bUncomp; straddleFlags = bFlags;
                }
                if (i == lastIdx)
                {
                    lastOffset = cursor; lastComp = bComp; lastUncomp = bUncomp; lastFlags = bFlags;
                }

                cursor += bComp;
            }

            if (!ValidateSingleBlock(stream, xorKey, rangeLength, firstOffset, firstComp, firstUncomp, firstFlags, out byte[]? firstDecoded))
            {
                return false;
            }
            if (hasFirstSerializedNode && firstDecoded != null && firstDecoded.Length >= 48
                && !UnityHeaderProbe.IsSerializedFileHeader(firstDecoded, firstNodeSize))
            {
                return false;
            }
            if (straddleIdx >= 0 && straddleIdx != firstIdx && straddleIdx != lastIdx
                && !ValidateSingleBlock(stream, xorKey, rangeLength, straddleOffset, straddleComp, straddleUncomp, straddleFlags, out _))
            {
                return false;
            }
            if (lastIdx != firstIdx
                && !ValidateSingleBlock(stream, xorKey, rangeLength, lastOffset, lastComp, lastUncomp, lastFlags, out _))
            {
                return false;
            }

            return true;
        }

        private static bool ValidateSingleBlock(
            Stream stream,
            byte[] xorKey,
            long rangeLength,
            long blockOffset,
            uint compSize,
            uint uncompSize,
            uint blockFlags,
            out byte[]? decodedBlock)
        {
            decodedBlock = null;
            var compType = (CompressionType)(blockFlags & (uint)StorageBlockFlags.CompressionTypeMask);
            if (compType == CompressionType.None)
            {
                if (compSize != uncompSize) return false;
                int inspectLen = (int)Math.Min(compSize, 128);
                byte[] rawBuf = new byte[inspectLen];
                if (!ReadRangeDecoded(stream, blockOffset, rawBuf, xorKey, rangeLength))
                {
                    return false;
                }
                decodedBlock = rawBuf;
                return true;
            }

            if (compType != CompressionType.Lz4 && compType != CompressionType.Lz4HC)
            {
                return true;
            }

            if (compSize > 4 * 1024 * 1024 || uncompSize > 16 * 1024 * 1024)
            {
                return true;
            }

            byte[] compBuf = new byte[compSize];
            if (!ReadRangeDecoded(stream, blockOffset, compBuf, xorKey, rangeLength))
            {
                return false;
            }

            byte[] uncompBuf = new byte[uncompSize];
            try
            {
                if (LZ4Codec.Decode(compBuf, uncompBuf) != (int)uncompSize)
                {
                    return false;
                }
                decodedBlock = uncompBuf;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool ReadRangeDecoded(
            Stream stream,
            long offset,
            byte[] destination,
            byte[] xorKey,
            long rangeLength)
        {
            stream.Position = offset;
            int read = ReadExactOrAvailable(stream, destination, 0, destination.Length);
            if (read != destination.Length)
            {
                return false;
            }

            long xorEnd = rangeLength < 0 ? stream.Length : Math.Min(stream.Length, rangeLength);
            long sliceEnd = Math.Min(offset + destination.Length, xorEnd);
            if (sliceEnd > offset)
            {
                int xorCount = (int)(sliceEnd - offset);
                UnityHeaderProbe.XorBytesRepeating(destination.AsSpan(0, xorCount), offset, xorKey);
            }

            return true;
        }

        private static int ReadExactOrAvailable(Stream stream, byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = stream.Read(buffer, offset + total, count - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        private static ushort ReadUInt16BE(ReadOnlySpan<byte> buf, int offset)
            => (ushort)((buf[offset] << 8) | buf[offset + 1]);

        private static uint ReadUInt32BE(ReadOnlySpan<byte> buf, int offset)
            => ((uint)buf[offset] << 24)
                | ((uint)buf[offset + 1] << 16)
                | ((uint)buf[offset + 2] << 8)
                | buf[offset + 3];

        private static long ReadInt64BE(ReadOnlySpan<byte> buf, int offset)
            => ((long)ReadUInt32BE(buf, offset) << 32) | ReadUInt32BE(buf, offset + 4);
    }
}
