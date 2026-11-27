using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using AssetStudio.Avalonia.ViewModels;
using AssetStudio.CLI;
using Xunit;

namespace AssetStudio.Tests
{
    [Collection("ResourceReaderXorHeuristic")]
    public sealed class CliAndCaptureIntegrationTests
    {
        private const string GameName = "Sample Game";
        private const string CacheDirectoryVariable = "ASSETSTUDIO_DECOMPRESSION_CACHE_DIR";
        private const string DisableCacheOption = "--no-decompression-cache";

        [Fact]
        public void Cli_ScanUsesUserProfilesByDefault_AndHonoursProfileOptions()
        {
            string root = RuntimeGameCaptureTests.CreateTempDirectory();
            var random = new Random(101);
            byte[] key = new byte[32];
            byte[] iv = new byte[16];
            random.NextBytes(key);
            random.NextBytes(iv);
            var profile = new DecryptionProfile
            {
                Id = "cli-local-test",
                Algorithm = "aes-cbc",
                KeyHex = Convert.ToHexString(key),
                IvHex = Convert.ToHexString(iv),
                Padding = "pkcs7",
                FileNamePattern = "*.enc",
                ValidateUnityHeader = true,
            };
            try
            {
                string input = Path.Combine(root, "input");
                Directory.CreateDirectory(input);
                byte[] plain = RuntimeGameCaptureTests.BuildLz4Bundle("CAB-cli", 3000, 5);
                File.WriteAllBytes(Path.Combine(input, "pack.enc"), RuntimeGameCaptureTests.EncryptAes(plain, key, iv, Array.Empty<byte>(), CipherMode.CBC, true, false));

                string userDirectory = Path.Combine(root, "userdata");
                string profilesDirectory = Path.Combine(userDirectory, "DecryptionProfiles");
                Directory.CreateDirectory(profilesDirectory);
                string userFile = Path.Combine(profilesDirectory, profile.Id + ".json");
                File.WriteAllText(userFile, JsonSerializer.Serialize(profile));
                string externalFile = Path.Combine(root, "external.json");
                File.WriteAllText(externalFile, JsonSerializer.Serialize(profile));

                string? previous = Environment.GetEnvironmentVariable(DecryptionHelper.UserDataDirectoryVariable);
                try
                {
                    Environment.SetEnvironmentVariable(DecryptionHelper.UserDataDirectoryVariable, userDirectory);
                    Assert.Equal(profilesDirectory, DecryptionHelper.GetUserDecryptionProfilesDirectory());

                    (int exit, string stdout, _) = InvokeCli("scan", "--no-local-profiles", input);
                    Assert.Equal(0, exit);
                    Assert.Contains("Encrypted: 1", stdout, StringComparison.Ordinal);
                    Assert.DoesNotContain("[UnityFS", stdout, StringComparison.Ordinal);

                    (exit, stdout, _) = InvokeCli("scan", input);
                    Assert.Equal(0, exit);
                    Assert.Contains("Encrypted: 0", stdout, StringComparison.Ordinal);
                    Assert.Contains("[UnityFS", stdout, StringComparison.Ordinal);
                    DecryptionHelper.RemoveProfile(profile.Id);

                    (exit, stdout, _) = InvokeCli("scan", "--no-local-profiles", "--profiles", externalFile, input);
                    Assert.Equal(0, exit);
                    Assert.Contains("[UnityFS", stdout, StringComparison.Ordinal);
                    DecryptionHelper.RemoveProfile(profile.Id);

                    (exit, _, string stderr) = InvokeCli("scan", "--no-local-profiles", "--profiles", Path.Combine(root, "missing.json"), input);
                    Assert.Equal(CliRunner.ExitInvalidArguments, exit);
                    Assert.Contains("does not exist", stderr, StringComparison.Ordinal);

                    (exit, _, stderr) = InvokeCli("scan", "--profiles");
                    Assert.Equal(CliRunner.ExitInvalidArguments, exit);
                    Assert.Contains("missing value", stderr, StringComparison.Ordinal);

                    Assert.Contains("--no-local-profiles", InvokeCli("--help").StdOut, StringComparison.Ordinal);
                }
                finally
                {
                    Environment.SetEnvironmentVariable(DecryptionHelper.UserDataDirectoryVariable, previous);
                }
            }
            finally
            {
                DecryptionHelper.RemoveProfile(profile.Id);
                RuntimeGameCaptureTests.TryDeleteDirectory(root);
            }
        }

        [Fact]
        public void Cli_CaptureKeys_ValidatesItsArguments()
        {
            string root = RuntimeGameCaptureTests.CreateTempDirectory();
            try
            {
                Assert.Equal(CliRunner.ExitInvalidArguments, InvokeCli("capture-keys").ExitCode);
                Assert.Equal(CliRunner.ExitInvalidArguments, InvokeCli("capture-keys", "--timeout", "abc", root).ExitCode);
                Assert.Equal(CliRunner.ExitInvalidArguments, InvokeCli("capture-keys", "--timeout", "1", root).ExitCode);
                Assert.Equal(CliRunner.ExitInvalidArguments, InvokeCli("capture-keys", "--format", "csv", root).ExitCode);
                Assert.Equal(CliRunner.ExitInvalidArguments, InvokeCli("capture-keys", "--exe").ExitCode);
                Assert.Equal(CliRunner.ExitInvalidArguments, InvokeCli("capture-keys", "--bogus", root).ExitCode);
                Assert.Equal(CliRunner.ExitInvalidArguments, InvokeCli("capture-keys", Path.Combine(root, "nowhere"), root).ExitCode);
                Assert.Equal(CliRunner.ExitInvalidArguments, InvokeCli("capture-keys", Path.Combine(root, "nowhere")).ExitCode);

                if (GameKeyCapture.IsSupported)
                {
                    (int exit, string stdout, string stderr) = InvokeCli("capture-keys", root);
                    Assert.Equal(CliRunner.ExitRuntimeError, exit);
                    Assert.Contains("--exe", stderr, StringComparison.Ordinal);
                }

                Assert.Contains("capture-keys", InvokeCli("--help").StdOut, StringComparison.Ordinal);
            }
            finally
            {
                RuntimeGameCaptureTests.TryDeleteDirectory(root);
            }
        }

        [Fact]
        public void Cli_ListReusesTheDecompressedCache_AndCanSkipIt()
        {
            string root = RuntimeGameCaptureTests.CreateTempDirectory();
            string? previousCache = Environment.GetEnvironmentVariable(CacheDirectoryVariable);
            try
            {
                string input = CreateEncryptedInput(root);
                string cacheDirectory = Path.Combine(root, "cache");
                Environment.SetEnvironmentVariable(CacheDirectoryVariable, cacheDirectory);
                string[] arguments = { "--no-local-profiles", "--profiles", Path.Combine(root, "profile.json") };

                (int exit, string first, string stderr) = InvokeCli(ArrayConcat("list", arguments, input));
                Assert.Equal(0, exit);
                Assert.Equal(string.Empty, stderr);
                Assert.Contains("Assets: 1", first, StringComparison.Ordinal);
                Assert.Contains(CacheAssetName, first, StringComparison.Ordinal);

                string blocks = LargestFile(cacheDirectory);
                var oldStamp = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(blocks, oldStamp);

                (exit, string second, _) = InvokeCli(ArrayConcat("list", arguments, input));
                Assert.Equal(0, exit);
                Assert.Equal(first, second);
                Assert.Equal(oldStamp, File.GetLastWriteTimeUtc(blocks));

                string freshDirectory = Path.Combine(root, "fresh");
                Environment.SetEnvironmentVariable(CacheDirectoryVariable, freshDirectory);
                (exit, string third, _) = InvokeCli(ArrayConcat("list", arguments, DisableCacheOption, input));
                Assert.Equal(0, exit);
                Assert.Equal(first, third);
                Assert.False(Directory.Exists(freshDirectory));

                string help = InvokeCli("--help").StdOut;
                Assert.Contains(DisableCacheOption, help, StringComparison.Ordinal);
                Assert.Contains(CacheDirectoryVariable, help, StringComparison.Ordinal);
            }
            finally
            {
                Environment.SetEnvironmentVariable(CacheDirectoryVariable, previousCache);
                DecryptionHelper.RemoveProfile(CacheTestProfileId);
                RuntimeGameCaptureTests.TryDeleteDirectory(root);
            }
        }

        [Fact]
        public void Cli_UnusableCacheFolder_FallsBackToNoCache()
        {
            string root = RuntimeGameCaptureTests.CreateTempDirectory();
            string? previousCache = Environment.GetEnvironmentVariable(CacheDirectoryVariable);
            try
            {
                string input = Path.Combine(root, "input");
                Directory.CreateDirectory(input);
                File.WriteAllBytes(Path.Combine(input, "pack.bundle"), RuntimeGameCaptureTests.BuildLz4Bundle("CAB-plain", BuildTextAssetPayload()));
                string blocker = Path.Combine(root, "blocker");
                File.WriteAllText(blocker, string.Empty);
                Environment.SetEnvironmentVariable(CacheDirectoryVariable, Path.Combine(blocker, "cache"));

                (int exit, string stdout, string stderr) = InvokeCli("list", "--no-local-profiles", input);
                Assert.Equal(0, exit);
                Assert.Contains("Assets: 1", stdout, StringComparison.Ordinal);
                Assert.Contains(CacheAssetName, stdout, StringComparison.Ordinal);
                Assert.Contains("decompression cache unavailable", stderr, StringComparison.Ordinal);
            }
            finally
            {
                Environment.SetEnvironmentVariable(CacheDirectoryVariable, previousCache);
                RuntimeGameCaptureTests.TryDeleteDirectory(root);
            }
        }

        private const string CacheTestProfileId = "cli-cache-test";

        private static string CreateEncryptedInput(string root)
        {
            var random = new Random(121);
            byte[] key = new byte[32];
            byte[] iv = new byte[16];
            random.NextBytes(key);
            random.NextBytes(iv);
            File.WriteAllText(Path.Combine(root, "profile.json"), JsonSerializer.Serialize(new DecryptionProfile
            {
                Id = CacheTestProfileId,
                Algorithm = "aes-cbc",
                KeyHex = Convert.ToHexString(key),
                IvHex = Convert.ToHexString(iv),
                Padding = "pkcs7",
                FileNamePattern = "*.enc",
                ValidateUnityHeader = true,
            }));

            string input = Path.Combine(root, "input");
            Directory.CreateDirectory(input);
            byte[] plain = RuntimeGameCaptureTests.BuildLz4Bundle("CAB-cache", BuildTextAssetPayload());
            File.WriteAllBytes(Path.Combine(input, "pack.enc"), RuntimeGameCaptureTests.EncryptAes(plain, key, iv, Array.Empty<byte>(), CipherMode.CBC, true, false));
            return input;
        }

        private const string CacheAssetName = "CliCache";

        private static byte[] BuildTextAssetPayload()
            => FormatsAndCliTests.BuildSerializedFileWithObjects(
                "2021.3.16f1",
                new[] { (30L, (int)ClassIDType.TextAsset, FormatsAndCliTests.BuildTextAssetBinary(CacheAssetName, "cached text"), true) });

        private static string[] ArrayConcat(string first, string[] middle, params string[] last)
            => new[] { first }.Concat(middle).Concat(last).ToArray();

        private static string LargestFile(string directory)
            => Directory.GetFiles(directory, "*", SearchOption.AllDirectories).OrderByDescending(path => new FileInfo(path).Length).First();

        [Fact]
        public void SavingACapturedProfile_ReplacesTheStaleOneForTheSameGame()
        {
            string root = RuntimeGameCaptureTests.CreateTempDirectory();
            string profilesDirectory = Path.Combine(root, "profiles");
            try
            {
                string bundleDirectory = RuntimeGameCaptureTests.CreateGameLayout(root);
                var random = new Random(111);
                CaptureResult firstCapture = Capture(bundleDirectory, random);
                GameCaptureTarget target = GameCaptureTarget.TryResolve(firstCapture.Files[0])!;
                DecryptionProfile first = GameKeyCapture.BuildProfile(target, firstCapture.Cipher);
                Assert.StartsWith(GameKeyCapture.CapturedProfilePrefix, first.Id, StringComparison.Ordinal);
                Assert.Equal(first.Id, GameKeyCapture.BuildProfile(target, firstCapture.Cipher).Id);

                string firstFile = GameKeyCapture.SaveProfile(first, profilesDirectory);
                Assert.Equal(Path.Combine(profilesDirectory, first.Id + ".json"), firstFile);

                CaptureResult secondCapture = Capture(bundleDirectory, random);
                DecryptionProfile second = GameKeyCapture.BuildProfile(target, secondCapture.Cipher);
                Assert.Equal(first.Id, second.Id);
                GameKeyCapture.SaveProfile(second, profilesDirectory);
                Assert.Equal(secondCapture.Key, Convert.FromHexString(DecryptionHelper.GetRegisteredProfiles().Single(p => p.Id == first.Id).KeyHex!));
                Assert.Single(Directory.GetFiles(profilesDirectory), file => Path.GetFileName(file).StartsWith(first.Id, StringComparison.Ordinal));

                var stale = new DecryptionProfile
                {
                    Id = GameKeyCapture.CapturedProfilePrefix + "aes-ctr-000000000000",
                    Algorithm = "aes-ctr",
                    KeyHex = Convert.ToHexString(secondCapture.Key),
                    IvFromStreamPrefix = true,
                    FileNamePattern = first.FileNamePattern,
                    PathPattern = first.PathPattern,
                    ValidateUnityHeader = true,
                };
                string staleFile = DecryptionHelper.SaveProfileToUserDirectory(stale, profilesDirectory);
                Assert.True(File.Exists(staleFile));

                GameKeyCapture.SaveProfile(first, profilesDirectory);
                Assert.DoesNotContain(DecryptionHelper.GetRegisteredProfiles(), p => p.Id == stale.Id);
                Assert.False(File.Exists(staleFile));
                Assert.Equal(firstCapture.Key, Convert.FromHexString(DecryptionHelper.GetRegisteredProfiles().Single(p => p.Id == first.Id).KeyHex!));
                Assert.Single(Directory.GetFiles(profilesDirectory));
            }
            finally
            {
                foreach (DecryptionProfile profile in DecryptionHelper.GetRegisteredProfiles().Where(GameKeyCapture.IsCapturedProfile))
                {
                    DecryptionHelper.RemoveProfile(profile.Id);
                }

                RuntimeGameCaptureTests.TryDeleteDirectory(root);
            }
        }

        [Fact]
        public void GameCaptureTarget_CanBeBuiltFromAChosenExecutable_ForFilesOutsideTheGameFolder()
        {
            string root = RuntimeGameCaptureTests.CreateTempDirectory();
            string? profileId = null;
            try
            {
                string gameDirectory = Path.Combine(root, "Games", GameName);
                string copies = Path.Combine(root, "copies", "EncryptedBundles");
                Directory.CreateDirectory(gameDirectory);
                Directory.CreateDirectory(copies);
                string executable = Path.Combine(gameDirectory, GameName + ".exe");
                File.WriteAllBytes(executable, new byte[] { 1, 2, 3 });
                CaptureResult capture = Capture(copies, new Random(121));

                Assert.Null(GameCaptureTarget.TryResolve(capture.Files[0]));
                GameCaptureTarget? target = GameCaptureTarget.FromExecutable(executable, capture.Files[0]);
                Assert.NotNull(target);
                Assert.Equal(gameDirectory, target!.GameDirectory);
                Assert.Equal(executable, target.ExecutablePath);
                Assert.Equal(2, target.SampleFiles.Count);
                Assert.Null(GameCaptureTarget.FromExecutable(Path.Combine(root, "missing.exe"), capture.Files[0]));
                Assert.Null(GameCaptureTarget.FromExecutable(executable, Path.Combine(root, "none.enc")));

                DecryptionProfile profile = GameKeyCapture.BuildProfile(target, capture.Cipher);
                Assert.True(profile.Validate(out string? error), error);
                Assert.Equal(copies.Replace('\\', '/') + "/*", profile.PathPattern);
                DecryptionHelper.RegisterProfile(profile);
                profileId = profile.Id;
                for (int i = 0; i < capture.Files.Length; i++)
                {
                    using var stream = File.OpenRead(capture.Files[i]);
                    using Stream? decoded = DecryptionHelper.CheckAndDecryptStream(stream, capture.Files[i], out string? providerId, out _);
                    Assert.Equal("DeclarativeProfile", providerId);
                    using var copy = new MemoryStream();
                    decoded!.CopyTo(copy);
                    Assert.Equal(capture.Plain[i], copy.ToArray());
                }
            }
            finally
            {
                if (profileId != null)
                {
                    DecryptionHelper.RemoveProfile(profileId);
                }

                RuntimeGameCaptureTests.TryDeleteDirectory(root);
            }
        }

        [Fact]
        public void DecryptionKeyDialog_LetsTheUserChooseTheGameExecutable()
        {
            string root = RuntimeGameCaptureTests.CreateTempDirectory();
            try
            {
                string gameDirectory = Path.Combine(root, "Games", GameName);
                string copies = Path.Combine(root, "copies");
                Directory.CreateDirectory(gameDirectory);
                Directory.CreateDirectory(copies);
                string executable = Path.Combine(gameDirectory, GameName + ".exe");
                File.WriteAllBytes(executable, new byte[] { 1, 2, 3 });
                string encrypted = Path.Combine(copies, "pack.enc");
                File.WriteAllBytes(encrypted, new byte[96]);
                var barrier = new StreamEncryptionBarrier("EncryptedBundleContainer", encrypted, "Encrypted container", 0.9, "AA BB");
                var viewModel = new DecryptionKeyDialogViewModel(
                    new DecryptionDialogContext(false, root, 0, 1, 0.0, new[] { barrier }),
                    userProfilesDirectoryOverride: root);

                Assert.True(viewModel.ShowGameCaptureSection);
                Assert.False(viewModel.HasCaptureTarget);
                Assert.Contains("No game was found", viewModel.CaptureGameSummaryText, StringComparison.Ordinal);
                Assert.False(viewModel.CaptureFromGameCommand.CanExecute(null));
                Assert.True(viewModel.ChooseCaptureExecutableCommand.CanExecute(null));

                viewModel.SetCaptureExecutable(Path.Combine(root, "missing.exe"));
                Assert.True(viewModel.HasValidationError);
                Assert.False(viewModel.HasCaptureTarget);

                viewModel.SetCaptureExecutable(executable);
                Assert.False(viewModel.HasValidationError);
                Assert.True(viewModel.HasCaptureTarget);
                Assert.Contains(GameName, viewModel.CaptureGameSummaryText, StringComparison.Ordinal);
                Assert.True(viewModel.CaptureFromGameCommand.CanExecute(null));

                viewModel.SetCaptureExecutable(null);
                Assert.True(viewModel.HasValidationError);
                Assert.True(viewModel.HasCaptureTarget);
            }
            finally
            {
                RuntimeGameCaptureTests.TryDeleteDirectory(root);
            }
        }

        [Fact]
        public void GameMemoryCapture_ScansTheMemoryOfARealProcess()
        {
            if (!GameKeyCapture.IsSupported)
            {
                return;
            }

            string root = RuntimeGameCaptureTests.CreateTempDirectory();
            var random = new Random(131);
            try
            {
                byte[] key = new byte[32];
                random.NextBytes(key);
                CaptureResult capture = Capture(root, random, key);
                var image = new RuntimeGameCaptureTests.ManagedImage(random);
                image.AddArray(key).AddArray(new byte[32]);
                image.AddDump(RuntimeGameCaptureTests.MakeDump(capture.Plain[1], 8184));
                image.AddDump(RuntimeGameCaptureTests.MakeDump(capture.Plain[0], capture.Plain[0].Length));
                byte[] pinnedBytes = image.ToArray();
                GCHandle pinned = GCHandle.Alloc(pinnedBytes, GCHandleType.Pinned);
                try
                {
                    long address = pinned.AddrOfPinnedObject().ToInt64();
                    GameMemoryScanResult result = GameMemoryCapture.ScanProcess(
                        Environment.ProcessId,
                        KeyValidationContext.Create(KeyValidationContext.LoadTails(capture.Files), capture.Files, null),
                        GameMemoryCapture.DefaultDumpBytes,
                        CancellationToken.None,
                        address & ~0xFFFL,
                        address + pinnedBytes.Length + 4096);

                    Assert.True(key.SequenceEqual(Assert.Single(result.Keys).Key));
                    Assert.Contains(result.Dumps, dump => dump.DeclaredSize == capture.Plain[1].Length
                        && dump.Data.AsSpan(0, 8184).SequenceEqual(capture.Plain[1].AsSpan(0, 8184)));
                    Assert.Contains(result.Dumps, dump => dump.DeclaredSize == capture.Plain[0].Length
                        && dump.Data.AsSpan(0, capture.Plain[0].Length).SequenceEqual(capture.Plain[0]));
                }
                finally
                {
                    pinned.Free();
                }

                GC.KeepAlive(pinnedBytes);
            }
            finally
            {
                RuntimeGameCaptureTests.TryDeleteDirectory(root);
            }
        }

        [Fact]
        public void Cli_CaptureKeys_CapturesFromARunningProcess_OnLinux()
        {
            if (!OperatingSystem.IsLinux() || !HasPerl())
            {
                return;
            }

            string root = RuntimeGameCaptureTests.CreateTempDirectory();
            string userDirectory = Path.Combine(root, "userdata");
            string? previous = Environment.GetEnvironmentVariable(DecryptionHelper.UserDataDirectoryVariable);
            try
            {
                Environment.SetEnvironmentVariable(DecryptionHelper.UserDataDirectoryVariable, userDirectory);
                var random = new Random(141);
                byte[] key = new byte[32];
                random.NextBytes(key);
                string gameDirectory = Path.Combine(root, "FakeGame");
                string bundleDirectory = Path.Combine(gameDirectory, "FakeGame_Data", "StreamingAssets", "EncryptedBundles");
                Directory.CreateDirectory(bundleDirectory);
                CaptureResult capture = Capture(bundleDirectory, random, key);
                var image = new RuntimeGameCaptureTests.ManagedImage(random);
                image.AddArray(key).AddArray(new byte[32]);
                image.AddDump(RuntimeGameCaptureTests.MakeDump(capture.Plain[1], 8184));
                image.AddDump(RuntimeGameCaptureTests.MakeDump(capture.Plain[0], capture.Plain[0].Length));
                File.WriteAllBytes(Path.Combine(gameDirectory, "image.bin"), image.ToArray());
                string executable = Path.Combine(gameDirectory, "FakeGame");
                File.WriteAllText(executable, "#!/bin/sh\nexec perl -e 'my $f = shift; open(my $h, \"<\", $f) or die $!; binmode $h; local $/; my $d = <$h>; $SIG{TERM} = sub { exit 0 }; $SIG{INT} = sub { exit 0 }; sleep 600;' \"$(dirname \"$0\")/image.bin\"\n");
                MakeExecutable(executable);
                GC.Collect();
                GC.WaitForPendingFinalizers();

                (int exit, string stdout, string stderr) = InvokeCli("capture-keys", bundleDirectory, "--timeout", "60", "--format", "json");
                Assert.True(exit == 0, stderr + stdout);
                using JsonDocument report = JsonDocument.Parse(stdout);
                Assert.Equal("Succeeded", report.RootElement.GetProperty("status").GetString());
                string? profilePath = report.RootElement.GetProperty("profilePath").GetString();
                Assert.NotNull(profilePath);
                Assert.StartsWith(userDirectory, profilePath, StringComparison.Ordinal);
                Assert.True(File.Exists(profilePath), profilePath);
                for (int i = 0; i < capture.Files.Length; i++)
                {
                    using var stream = File.OpenRead(capture.Files[i]);
                    using Stream? decoded = DecryptionHelper.CheckAndDecryptStream(stream, capture.Files[i], out string? providerId, out _);
                    Assert.Equal("DeclarativeProfile", providerId);
                    using var copy = new MemoryStream();
                    decoded!.CopyTo(copy);
                    Assert.Equal(capture.Plain[i], copy.ToArray());
                }

                Assert.False(GameKeyCapture.IsRunning(GameCaptureTarget.TryResolve(capture.Files[0])!));
            }
            finally
            {
                Environment.SetEnvironmentVariable(DecryptionHelper.UserDataDirectoryVariable, previous);
                foreach (DecryptionProfile profile in DecryptionHelper.GetRegisteredProfiles().Where(GameKeyCapture.IsCapturedProfile))
                {
                    DecryptionHelper.RemoveProfile(profile.Id);
                }

                RuntimeGameCaptureTests.TryDeleteDirectory(root);
            }
        }

        private sealed record CaptureResult(CapturedCipher Cipher, byte[] Key, string[] Files, byte[][] Plain);

        private static CaptureResult Capture(string directory, Random random, byte[]? key = null)
        {
            key ??= new byte[32];
            random.NextBytes(key);
            byte[] iv = new byte[16];
            byte[] mask = new byte[256];
            random.NextBytes(iv);
            random.NextBytes(mask);
            byte[][] plain = { RuntimeGameCaptureTests.BuildBundle(2800, 1, 9), RuntimeGameCaptureTests.BuildBundle(20000, 1, 8) };
            string[] files = { Path.Combine(directory, "small.enc"), Path.Combine(directory, "big.enc") };
            for (int i = 0; i < files.Length; i++)
            {
                File.WriteAllBytes(files[i], RuntimeGameCaptureTests.EncryptAes(plain[i], key, iv, mask, CipherMode.CBC, true, false));
            }

            var image = new RuntimeGameCaptureTests.ManagedImage(random);
            image.AddArray(key).AddArray(new byte[32]);
            image.AddDump(RuntimeGameCaptureTests.MakeDump(plain[1], 8184));
            image.AddDump(RuntimeGameCaptureTests.MakeDump(plain[0], plain[0].Length));
            var keys = new Dictionary<string, CapturedKey>(StringComparer.Ordinal);
            var dumps = new Dictionary<string, PlaintextBundleDump>(StringComparer.Ordinal);
            GameMemoryCapture.ScanBuffer(
                image.ToArray(),
                KeyValidationContext.Create(KeyValidationContext.LoadTails(files), files, null),
                GameMemoryCapture.DefaultDumpBytes,
                keys,
                dumps);
            CapturedKey capturedKey = Assert.Single(keys.Values);
            Assert.True(key.SequenceEqual(capturedKey.Key));
            Assert.True(RuntimeCipherRecovery.TryRecover(capturedKey, files, dumps.Values.ToList(), out CapturedCipher? cipher));
            return new CaptureResult(cipher!, key, files, plain);
        }

        private static (int ExitCode, string StdOut, string StdErr) InvokeCli(params string[] args)
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            int exitCode = CliRunner.Run(args, stdout, stderr);
            return (exitCode, stdout.ToString(), stderr.ToString());
        }

        private static bool HasPerl()
        {
            try
            {
                using Process? process = Process.Start(new ProcessStartInfo("perl", "-e1") { UseShellExecute = false, RedirectStandardOutput = true });
                if (process == null)
                {
                    return false;
                }

                process.StandardOutput.ReadToEnd();
                return process.WaitForExit(5000) && process.ExitCode == 0;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                return false;
            }
        }

        [SupportedOSPlatform("linux")]
        private static void MakeExecutable(string path)
            => File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
