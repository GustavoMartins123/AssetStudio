using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Permissions;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;

[assembly: CompilationRelaxations(8)]
[assembly: RuntimeCompatibility(WrapNonExceptionThrows = true)]
[assembly: Debuggable(DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints)]
[assembly: TargetFramework(".NETCoreApp,Version=v8.0", FrameworkDisplayName = ".NET 8.0")]
[assembly: AssemblyCompany("jojomondag")]
[assembly: AssemblyConfiguration("Release")]
[assembly: AssemblyDescription("Core FFmpeg video player logic without UI dependencies. Use this package for non-Avalonia projects or custom UI implementations.")]
[assembly: AssemblyFileVersion("2.7.0.0")]
[assembly: AssemblyInformationalVersion("2.7.0+651141a9263a3fdc2de2fde242f60de53b54b485")]
[assembly: AssemblyProduct("FFmpegVideoPlayer.Core")]
[assembly: AssemblyTitle("FFmpegVideoPlayer.Core")]
[assembly: AssemblyMetadata("RepositoryUrl", "https://github.com/jojomondag/FFmpegVideoPlayer.Avalonia")]
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
[assembly: AssemblyVersion("2.7.0.0")]
[module: UnverifiableCode]
[module: RefSafetyRules(11)]
namespace FFmpegVideoPlayer.Core;

public static class FFmpegInitializer
{
	private static bool _isInitialized;

	private static string? _ffmpegPath;

	private static string? _initializationError;

	public static bool IsInitialized => _isInitialized;

	public static string? FFmpegPath => _ffmpegPath;

	public static string? InitializationError => _initializationError;

	public static string PlatformInfo => GetPlatformName() + "-" + GetArchitectureName();

	public static bool IsArm
	{
		get
		{
			if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
			{
				return RuntimeInformation.ProcessArchitecture == Architecture.Arm;
			}
			return true;
		}
	}

	public static bool IsX64 => RuntimeInformation.ProcessArchitecture == Architecture.X64;

	public static bool IsX86 => RuntimeInformation.ProcessArchitecture == Architecture.X86;

	public static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

	public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

	public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

	public static event Action<string>? StatusChanged;

	private static string GetPlatformName()
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			return "windows";
		}
		if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
		{
			return "macos";
		}
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			return "linux";
		}
		return "unknown";
	}

	private static string GetArchitectureName()
	{
		return RuntimeInformation.ProcessArchitecture switch
		{
			Architecture.X64 => "x64", 
			Architecture.X86 => "x86", 
			Architecture.Arm64 => "arm64", 
			Architecture.Arm => "arm", 
			_ => "unknown", 
		};
	}

	public static bool Initialize(string? customPath = null, bool autoInstall = true, bool useBundledBinaries = true)
	{
		if (_isInitialized)
		{
			return true;
		}
		try
		{
			FFmpegInitializer.StatusChanged?.Invoke("Initializing FFmpeg for " + PlatformInfo + "...");
			if (!string.IsNullOrEmpty(customPath) && Directory.Exists(customPath) && FFmpegPathResolver.HasFFmpegLibrary(customPath))
			{
				FFmpegPathResolver.ConfigureNativeSearchPath(customPath);
				if (!FFmpegPathResolver.TryValidateBindings())
				{
					throw new FFmpegNotFoundException($"FFmpeg libraries at custom path '{customPath}' failed to load. The files are present but dlopen/LoadLibrary could not resolve their dependencies (common on macOS when dylibs hardcode Homebrew paths not present on this machine).\n" + GetInstallationInstructions());
				}
				_ffmpegPath = customPath;
			}
			if (string.IsNullOrEmpty(_ffmpegPath) && useBundledBinaries)
			{
				string text = FFmpegPathResolver.TryConfigureBundledFFmpeg();
				if (!string.IsNullOrEmpty(text))
				{
					if (FFmpegPathResolver.TryValidateBindings())
					{
						_ffmpegPath = text;
					}
					else
					{
						FFmpegInitializer.StatusChanged?.Invoke("Bundled FFmpeg failed to load — searching system…");
					}
				}
			}
			if (string.IsNullOrEmpty(_ffmpegPath) && string.IsNullOrEmpty(customPath))
			{
				string text2 = FindFFmpegPath(null);
				if (!string.IsNullOrEmpty(text2))
				{
					FFmpegPathResolver.ConfigureNativeSearchPath(text2);
					if (FFmpegPathResolver.TryValidateBindings())
					{
						_ffmpegPath = text2;
					}
				}
			}
			if (string.IsNullOrEmpty(_ffmpegPath) && IsMacOS && autoInstall)
			{
				FFmpegInitializer.StatusChanged?.Invoke("FFmpeg not found. Installing via Homebrew (this may take a few minutes)...");
				if (TryInstallFFmpegOnMacOS())
				{
					string text3 = FindFFmpegPath(null);
					if (!string.IsNullOrEmpty(text3))
					{
						FFmpegPathResolver.ConfigureNativeSearchPath(text3);
						if (FFmpegPathResolver.TryValidateBindings())
						{
							_ffmpegPath = text3;
						}
					}
				}
			}
			if (string.IsNullOrEmpty(_ffmpegPath) && IsLinux && autoInstall)
			{
				FFmpegInitializer.StatusChanged?.Invoke("FFmpeg not found. Attempting install via system package manager…");
				if (TryInstallFFmpegOnLinux())
				{
					string text4 = FindFFmpegPath(null);
					if (!string.IsNullOrEmpty(text4))
					{
						FFmpegPathResolver.ConfigureNativeSearchPath(text4);
						if (FFmpegPathResolver.TryValidateBindings())
						{
							_ffmpegPath = text4;
						}
					}
				}
			}
			if (string.IsNullOrEmpty(_ffmpegPath))
			{
				FFmpegPathResolver.InitializeBindings();
			}
			string text5 = "unknown";
			uint num = 0u;
			try
			{
				num = ffmpeg.avcodec_version();
				if (num != 0)
				{
					text5 = $"{num >> 16}.{(num >> 8) & 0xFF}.{num & 0xFF}";
				}
			}
			catch
			{
			}
			if (num == 0)
			{
				throw new FFmpegNotFoundException((string.IsNullOrEmpty(_ffmpegPath) ? "FFmpeg libraries could not be located or loaded." : ("FFmpeg libraries at '" + _ffmpegPath + "' loaded but no functions resolved — the native library is likely incompatible or missing transitive dependencies.")) + "\n" + GetInstallationInstructions());
			}
			_isInitialized = true;
			FFmpegInitializer.StatusChanged?.Invoke("FFmpeg initialized successfully (libavcodec: " + text5 + ")");
			return true;
		}
		catch (DllNotFoundException ex)
		{
			_initializationError = "FFmpeg libraries not found: " + ex.Message;
			FFmpegInitializer.StatusChanged?.Invoke(_initializationError);
			throw new FFmpegNotFoundException("FFmpeg libraries not found.\n" + GetInstallationInstructions(), ex);
		}
		catch (Exception ex2)
		{
			_initializationError = ex2.Message;
			FFmpegInitializer.StatusChanged?.Invoke("Failed to initialize FFmpeg: " + ex2.Message);
			throw new FFmpegNotFoundException("Failed to initialize FFmpeg: " + ex2.Message + "\n" + GetInstallationInstructions(), ex2);
		}
	}

	public static async Task<bool> InitializeAsync(string? customPath = null, bool autoInstall = true, bool useBundledBinaries = true)
	{
		if (_isInitialized)
		{
			return true;
		}
		return await Task.Run(() => Initialize(customPath, autoInstall, useBundledBinaries));
	}

	public static bool TryInitialize(string? customPath, out string? errorMessage, bool autoInstall = true, bool useBundledBinaries = true)
	{
		try
		{
			Initialize(customPath, autoInstall, useBundledBinaries);
			errorMessage = null;
			return true;
		}
		catch (FFmpegNotFoundException ex)
		{
			errorMessage = ex.Message;
			_initializationError = ex.Message;
			return false;
		}
		catch (Exception ex2)
		{
			errorMessage = ex2.Message;
			_initializationError = ex2.Message;
			return false;
		}
	}

	private static string? GetHomebrewPath()
	{
		if (File.Exists("/opt/homebrew/bin/brew"))
		{
			return "/opt/homebrew/bin/brew";
		}
		if (File.Exists("/usr/local/bin/brew"))
		{
			return "/usr/local/bin/brew";
		}
		return null;
	}

	public static bool TryInstallFFmpegOnMacOS()
	{
		if (!IsMacOS)
		{
			return false;
		}
		string homebrewPath = GetHomebrewPath();
		if (homebrewPath == null)
		{
			FFmpegInitializer.StatusChanged?.Invoke("Installing Homebrew...");
			if (!TryInstallHomebrew())
			{
				FFmpegInitializer.StatusChanged?.Invoke("Failed to install Homebrew. Please install manually.");
				return false;
			}
			homebrewPath = GetHomebrewPath();
			if (homebrewPath == null)
			{
				return false;
			}
		}
		FFmpegInitializer.StatusChanged?.Invoke("Installing FFmpeg via Homebrew (this may take several minutes)...");
		try
		{
			ProcessStartInfo processStartInfo = new ProcessStartInfo
			{
				FileName = homebrewPath,
				Arguments = "install ffmpeg",
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			if (IsArm)
			{
				processStartInfo.Environment["PATH"] = "/opt/homebrew/bin:/opt/homebrew/sbin:" + Environment.GetEnvironmentVariable("PATH");
			}
			else
			{
				processStartInfo.Environment["PATH"] = "/usr/local/bin:/usr/local/sbin:" + Environment.GetEnvironmentVariable("PATH");
			}
			using Process process = Process.Start(processStartInfo);
			if (process == null)
			{
				return false;
			}
			string text = process.StandardOutput.ReadToEnd();
			string text2 = process.StandardError.ReadToEnd();
			process.WaitForExit();
			if (process.ExitCode == 0)
			{
				FFmpegInitializer.StatusChanged?.Invoke("FFmpeg installed successfully!");
				return true;
			}
			if (text2.Contains("already installed") || text.Contains("already installed"))
			{
				return true;
			}
			FFmpegInitializer.StatusChanged?.Invoke("FFmpeg installation failed: " + text2);
			return false;
		}
		catch (Exception ex)
		{
			FFmpegInitializer.StatusChanged?.Invoke("FFmpeg installation error: " + ex.Message);
			return false;
		}
	}

	private static bool TryInstallHomebrew()
	{
		try
		{
			ProcessStartInfo processStartInfo = new ProcessStartInfo();
			processStartInfo.FileName = "/bin/bash";
			processStartInfo.Arguments = "-c \"$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)\"";
			processStartInfo.RedirectStandardOutput = true;
			processStartInfo.RedirectStandardError = true;
			processStartInfo.RedirectStandardInput = true;
			processStartInfo.UseShellExecute = false;
			processStartInfo.CreateNoWindow = true;
			processStartInfo.Environment["NONINTERACTIVE"] = "1";
			using Process process = Process.Start(processStartInfo);
			if (process == null)
			{
				return false;
			}
			process.StandardInput.Close();
			process.StandardOutput.ReadToEnd();
			process.StandardError.ReadToEnd();
			process.WaitForExit();
			if (process.ExitCode == 0)
			{
				FFmpegInitializer.StatusChanged?.Invoke("Homebrew installed successfully!");
				return true;
			}
			return false;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static bool TryInstallFFmpegOnLinux()
	{
		if (!IsLinux)
		{
			return false;
		}
		string text = null;
		string text2;
		string text3;
		if (File.Exists("/usr/bin/apt-get"))
		{
			text = "/usr/bin/apt-get";
			text2 = "install -y ffmpeg libavcodec-dev libavformat-dev libavutil-dev libswscale-dev libswresample-dev";
			text3 = "sudo apt install -y ffmpeg libavcodec-dev libavformat-dev libavutil-dev libswscale-dev libswresample-dev";
		}
		else if (File.Exists("/usr/bin/dnf"))
		{
			text = "/usr/bin/dnf";
			text2 = "install -y ffmpeg ffmpeg-devel";
			text3 = "sudo dnf install -y ffmpeg ffmpeg-devel";
		}
		else
		{
			if (!File.Exists("/usr/bin/pacman"))
			{
				FFmpegInitializer.StatusChanged?.Invoke("No supported Linux package manager found. Install FFmpeg manually.");
				return false;
			}
			text = "/usr/bin/pacman";
			text2 = "-S --noconfirm ffmpeg";
			text3 = "sudo pacman -S ffmpeg";
		}
		bool flag = string.Equals(Environment.UserName, "root", StringComparison.Ordinal);
		FFmpegInitializer.StatusChanged?.Invoke("Installing FFmpeg (this may take a few minutes)...");
		try
		{
			ProcessStartInfo processStartInfo = new ProcessStartInfo();
			processStartInfo.FileName = (flag ? text : "sudo");
			processStartInfo.Arguments = (flag ? text2 : ("-n " + text + " " + text2));
			processStartInfo.RedirectStandardOutput = true;
			processStartInfo.RedirectStandardError = true;
			processStartInfo.UseShellExecute = false;
			processStartInfo.CreateNoWindow = true;
			processStartInfo.Environment["DEBIAN_FRONTEND"] = "noninteractive";
			using Process process = Process.Start(processStartInfo);
			if (process == null)
			{
				return false;
			}
			process.StandardOutput.ReadToEnd();
			string text4 = process.StandardError.ReadToEnd();
			process.WaitForExit();
			if (process.ExitCode == 0)
			{
				FFmpegInitializer.StatusChanged?.Invoke("FFmpeg installed successfully!");
				return true;
			}
			string text5 = text4.ToLowerInvariant();
			if (text5.Contains("password is required") || text5.Contains("a terminal is required") || text5.Contains("sudo:"))
			{
				FFmpegInitializer.StatusChanged?.Invoke("FFmpeg auto-install needs sudo. Run manually: " + text3);
			}
			else
			{
				FFmpegInitializer.StatusChanged?.Invoke("FFmpeg install failed. Run manually: " + text3);
			}
			return false;
		}
		catch (Exception ex)
		{
			FFmpegInitializer.StatusChanged?.Invoke("FFmpeg install error: " + ex.Message + ". Run manually: " + text3);
			return false;
		}
	}

	public static string GetInstallationInstructions()
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			return "\r\nWINDOWS:\r\nInstall FFmpeg using one of these methods:\r\n\r\nOption 1 - WinGet (Recommended for Windows 11):\r\n    winget install ffmpeg\r\n\r\nOption 2 - Chocolatey:\r\n    choco install ffmpeg\r\n\r\nOption 3 - Manual Installation:\r\n    1. Download from https://www.gyan.dev/ffmpeg/builds/ (get the 'full' build)\r\n    2. Extract to a folder (e.g., C:\\ffmpeg)\r\n    3. Add C:\\ffmpeg\\bin to your system PATH\r\n    \r\nAfter installation, restart your terminal/IDE.";
		}
		if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
		{
			return "\r\nmacOS (Intel x64 and Apple Silicon ARM64):\r\nInstall FFmpeg using Homebrew (supports both architectures):\r\n\r\n    brew install ffmpeg\r\n\r\nIf you don't have Homebrew installed:\r\n    /bin/bash -c \"$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)\"\r\n    \r\nAfter installation, restart your terminal/IDE.";
		}
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			string architectureName = GetArchitectureName();
			return "\r\nLINUX (" + architectureName + "):\r\nInstall FFmpeg using your package manager:\r\n\r\nDebian/Ubuntu:\r\n    sudo apt update\r\n    sudo apt install ffmpeg libavcodec-dev libavformat-dev libavutil-dev libswscale-dev libswresample-dev\r\n\r\nFedora:\r\n    sudo dnf install ffmpeg ffmpeg-devel\r\n\r\nArch Linux:\r\n    sudo pacman -S ffmpeg\r\n\r\nAfter installation, restart your terminal/IDE.";
		}
		return "FFmpeg libraries not found. Please install FFmpeg on your system.";
	}

	public static FFmpegInstallationStatus CheckInstallation()
	{
		FFmpegInstallationStatus fFmpegInstallationStatus = new FFmpegInstallationStatus
		{
			Platform = GetPlatformName(),
			Architecture = GetArchitectureName()
		};
		try
		{
			string text = FindFFmpegPath(null);
			if (!string.IsNullOrEmpty(text))
			{
				fFmpegInstallationStatus.IsInstalled = true;
				fFmpegInstallationStatus.LibraryPath = text;
			}
			else
			{
				try
				{
					ffmpeg.RootPath = "";
					ffmpeg.av_version_info();
					fFmpegInstallationStatus.IsInstalled = true;
					fFmpegInstallationStatus.LibraryPath = "System default";
				}
				catch
				{
					fFmpegInstallationStatus.IsInstalled = false;
				}
			}
			fFmpegInstallationStatus.IsArchitectureCompatible = true;
		}
		catch (Exception ex)
		{
			fFmpegInstallationStatus.Error = ex.Message;
		}
		fFmpegInstallationStatus.InstallationInstructions = GetInstallationInstructions();
		return fFmpegInstallationStatus;
	}

	private static string? FindFFmpegPath(string? customPath)
	{
		string text = FindBundledFFmpeg();
		if (!string.IsNullOrEmpty(text))
		{
			return text;
		}
		string baseDirectory = AppContext.BaseDirectory;
		string[] array = new string[3]
		{
			Path.Combine(baseDirectory, "ffmpeg"),
			Path.Combine(baseDirectory, "lib"),
			baseDirectory
		};
		foreach (string text2 in array)
		{
			if (FFmpegPathResolver.HasFFmpegLibrary(text2))
			{
				return text2;
			}
		}
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			return FindWindowsFFmpeg();
		}
		if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
		{
			return FindMacOSFFmpeg();
		}
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			return FindLinuxFFmpeg();
		}
		return null;
	}

	private static string? FindBundledFFmpeg()
	{
		return FFmpegPathResolver.TryConfigureBundledFFmpeg();
	}

	private static string? FindWindowsFFmpeg()
	{
		string[] array = new string[5]
		{
			"C:\\ffmpeg\\bin",
			"C:\\Program Files\\ffmpeg\\bin",
			"C:\\Program Files (x86)\\ffmpeg\\bin",
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ffmpeg", "bin"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin")
		};
		foreach (string text in array)
		{
			if (FFmpegPathResolver.HasFFmpegLibrary(text))
			{
				return text;
			}
		}
		string environmentVariable = Environment.GetEnvironmentVariable("PATH");
		if (!string.IsNullOrEmpty(environmentVariable))
		{
			array = environmentVariable.Split(';');
			foreach (string text2 in array)
			{
				if (FFmpegPathResolver.HasFFmpegLibrary(text2))
				{
					return text2;
				}
			}
		}
		return null;
	}

	private static string? FindMacOSFFmpeg()
	{
		string[] array = new string[4] { "/opt/homebrew/lib", "/usr/local/lib", "/opt/homebrew/Cellar/ffmpeg", "/usr/local/Cellar/ffmpeg" };
		foreach (string text in array)
		{
			if (FFmpegPathResolver.HasFFmpegLibrary(text))
			{
				return text;
			}
			if (!text.Contains("Cellar") || !Directory.Exists(text))
			{
				continue;
			}
			try
			{
				string[] directories = Directory.GetDirectories(text);
				for (int j = 0; j < directories.Length; j++)
				{
					string text2 = Path.Combine(directories[j], "lib");
					if (FFmpegPathResolver.HasFFmpegLibrary(text2))
					{
						return text2;
					}
				}
			}
			catch
			{
			}
		}
		if (FFmpegPathResolver.HasFFmpegLibrary("/opt/local/lib"))
		{
			return "/opt/local/lib";
		}
		return null;
	}

	private static string? FindLinuxFFmpeg()
	{
		List<string> list = new List<string>();
		if (IsArm)
		{
			list.AddRange(new string[2] { "/usr/lib/aarch64-linux-gnu", "/usr/lib64" });
		}
		else
		{
			list.AddRange(new string[2] { "/usr/lib/x86_64-linux-gnu", "/usr/lib64" });
		}
		list.AddRange(new string[3] { "/usr/lib", "/usr/local/lib", "/lib" });
		foreach (string item in list)
		{
			if (FFmpegPathResolver.HasFFmpegLibrary(item))
			{
				return item;
			}
		}
		string environmentVariable = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
		if (!string.IsNullOrEmpty(environmentVariable))
		{
			string[] array = environmentVariable.Split(':');
			foreach (string text in array)
			{
				if (FFmpegPathResolver.HasFFmpegLibrary(text))
				{
					return text;
				}
			}
		}
		return null;
	}

	[Conditional("DEBUG")]
	private static void Log(string message)
	{
		Console.WriteLine("[FFmpegInitializer] " + message);
	}
}
public class FFmpegNotFoundException : Exception
{
	public FFmpegNotFoundException(string message)
		: base(message)
	{
	}

	public FFmpegNotFoundException(string message, Exception inner)
		: base(message, inner)
	{
	}
}
public class FFmpegInstallationStatus
{
	public string Platform { get; set; } = "";

	public string Architecture { get; set; } = "";

	public bool IsInstalled { get; set; }

	public bool IsArchitectureCompatible { get; set; }

	public string? LibraryPath { get; set; }

	public string? Error { get; set; }

	public string InstallationInstructions { get; set; } = "";

	public bool IsReady
	{
		get
		{
			if (IsInstalled)
			{
				return IsArchitectureCompatible;
			}
			return false;
		}
	}

	public override string ToString()
	{
		if (IsReady)
		{
			return "FFmpeg is installed and ready at: " + LibraryPath;
		}
		if (IsInstalled && !IsArchitectureCompatible)
		{
			return "FFmpeg is installed at " + LibraryPath + " but is not compatible with " + Architecture;
		}
		return "FFmpeg is not installed.\n" + InstallationInstructions;
	}
}
public sealed class FFmpegMediaPlayer : IDisposable
{
	private sealed class CachedFrame
	{
		public byte[] Data { get; }

		public int Width { get; }

		public int Height { get; }

		public int Stride { get; }

		public int DataLength { get; }

		public double Pts { get; }

		public CachedFrame(byte[] data, int width, int height, int stride, int dataLength, double pts)
		{
			Data = data;
			Width = width;
			Height = height;
			Stride = stride;
			DataLength = dataLength;
			Pts = pts;
		}
	}

	private unsafe AVFormatContext* _formatContext;

	private unsafe AVCodecContext* _videoCodecContext;

	private unsafe AVCodecContext* _audioCodecContext;

	private unsafe SwsContext* _swsContext;

	private unsafe AVFrame* _frame;

	private unsafe AVFrame* _rgbFrame;

	private unsafe AVPacket* _packet;

	private int _videoStreamIndex = -1;

	private int _audioStreamIndex = -1;

	private unsafe byte* _rgbBuffer;

	private int _rgbBufferSize;

	private Thread? _playbackThread;

	private CancellationTokenSource? _cancellationTokenSource;

	private bool _isPlaying;

	private bool _isPaused;

	private bool _isDisposed;

	private double _position;

	private double _duration;

	private int _volume = 100;

	private readonly object _lock = new object();

	private int _videoWidth;

	private int _videoHeight;

	private double _frameRate;

	private IAudioPlayer? _audioPlayer;

	private readonly Func<int, int, IAudioPlayer?>? _audioPlayerFactory;

	private unsafe SwrContext* _swrContext;

	private const int MaxPendingFrames = 4;

	private int _pendingFrameCount;

	private int _droppedFrames;

	private double _startTime;

	private double _playbackStartWallTime;

	private double _audioStartPts;

	private bool _needsResync;

	private double _audioClock;

	private double _videoClock;

	private AVRational _videoTimeBase;

	private AVRational _audioTimeBase;

	private double _pauseStartTime;

	private double _totalPauseTime;

	private const double SeekTargetEpsilon = 0.001;

	private double _seekTargetPts = -1.0;

	private bool _stepMode;

	private readonly Queue<CachedFrame> _frameCache = new Queue<CachedFrame>();

	private const int MaxCachedFrames = 30;

	private double _lastFramePts = -1.0;

	private readonly PlayerLogger _logger = new PlayerLogger();

	private readonly Action<Action>? _synchronizationCallback;

	public IAudioPlayer? AudioPlayer
	{
		get
		{
			return _audioPlayer;
		}
		set
		{
			lock (_lock)
			{
				_audioPlayer?.Dispose();
				_audioPlayer = value;
				if (_audioPlayer != null)
				{
					_audioPlayer.SetVolume((float)_volume / 100f);
					SyncAudioPlayerState();
				}
			}
		}
	}

	public bool IsPlaying
	{
		get
		{
			if (_isPlaying)
			{
				return !_isPaused;
			}
			return false;
		}
	}

	public float Position
	{
		get
		{
			if (!(_duration > 0.0))
			{
				return 0f;
			}
			return (float)(_position / _duration);
		}
	}

	public long Length => (long)(_duration * 1000.0);

	public int Volume
	{
		get
		{
			return _volume;
		}
		set
		{
			int volume = _volume;
			_volume = Math.Clamp(value, 0, 100);
			_audioPlayer?.SetVolume((float)_volume / 100f);
			_logger.Log("FFmpegMediaPlayer", "VolumeChanged", new
			{
				OldVolume = volume,
				NewVolume = _volume
			});
		}
	}

	public int VideoWidth => _videoWidth;

	public int VideoHeight => _videoHeight;

	public event EventHandler<PositionChangedEventArgs>? PositionChanged;

	public event EventHandler<LengthChangedEventArgs>? LengthChanged;

	public event EventHandler<FrameEventArgs>? FrameReady;

	public event EventHandler? Playing;

	public event EventHandler? Paused;

	public event EventHandler? Stopped;

	public event EventHandler? EndReached;

	public FFmpegMediaPlayer(Action<Action>? synchronizationCallback = null, Func<int, int, IAudioPlayer?>? audioPlayerFactory = null)
	{
		_synchronizationCallback = synchronizationCallback;
		_audioPlayerFactory = audioPlayerFactory;
	}

	public unsafe bool Open(string path)
	{
		lock (_lock)
		{
			_logger.Clear();
			_logger.Log("FFmpegMediaPlayer", "MovieLoadingStarted", new
			{
				Path = path,
				Timestamp = DateTime.Now
			});
			CloseInternal();
			_pendingFrameCount = 0;
			_droppedFrames = 0;
			_logger.Log("FFmpegMediaPlayer", "OpeningMediaFile", new
			{
				Path = path
			});
			fixed (AVFormatContext** formatContext = &_formatContext)
			{
				if (ffmpeg.avformat_open_input(formatContext, path, null, null) != 0)
				{
					_logger.Log("FFmpegMediaPlayer", "OpenFailed", new
					{
						Path = path,
						Reason = "avformat_open_input failed"
					});
					return false;
				}
			}
			_logger.Log("FFmpegMediaPlayer", "MediaFileOpened", new
			{
				Path = path
			});
			_logger.Log("FFmpegMediaPlayer", "ReadingStreamInfo");
			if (ffmpeg.avformat_find_stream_info(_formatContext, null) < 0)
			{
				_logger.Log("FFmpegMediaPlayer", "StreamInfoFailed", new
				{
					Reason = "avformat_find_stream_info failed"
				});
				CloseInternal();
				return false;
			}
			Dictionary<string, object> dictionary = new Dictionary<string, object>
			{
				["TotalStreams"] = _formatContext->nb_streams,
				["Duration"] = ((_formatContext->duration != ffmpeg.AV_NOPTS_VALUE) ? ((double)_formatContext->duration / 1000000.0) : 0.0),
				["FormatName"] = Marshal.PtrToStringAnsi((nint)_formatContext->iformat->name) ?? "unknown",
				["FormatLongName"] = Marshal.PtrToStringAnsi((nint)_formatContext->iformat->long_name) ?? "unknown",
				["BitRate"] = ((_formatContext->bit_rate > 0) ? _formatContext->bit_rate : 0),
				["StartTime"] = ((_formatContext->start_time != ffmpeg.AV_NOPTS_VALUE) ? ((double)_formatContext->start_time / 1000000.0) : 0.0)
			};
			try
			{
				FileInfo fileInfo = new FileInfo(path);
				dictionary["FileSize"] = fileInfo.Length;
				dictionary["FileSizeMB"] = Math.Round((double)fileInfo.Length / 1048576.0, 2);
			}
			catch
			{
				dictionary["FileSize"] = "unknown";
			}
			Dictionary<string, string> dictionary2 = new Dictionary<string, string>();
			if (_formatContext->metadata != null)
			{
				AVDictionaryEntry* ptr = null;
				while ((ptr = ffmpeg.av_dict_get(_formatContext->metadata, "", ptr, 2)) != null)
				{
					string text = Marshal.PtrToStringAnsi((nint)ptr->key) ?? "";
					string value = Marshal.PtrToStringAnsi((nint)ptr->value) ?? "";
					if (!string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(value))
					{
						dictionary2[text] = value;
					}
				}
			}
			if (dictionary2.Count > 0)
			{
				dictionary["Metadata"] = dictionary2;
			}
			List<object> list = new List<object>();
			for (int i = 0; i < _formatContext->nb_streams; i++)
			{
				AVStream* ptr2 = _formatContext->streams[i];
				AVCodecParameters* codecpar = ptr2->codecpar;
				Dictionary<string, object> dictionary3 = new Dictionary<string, object>
				{
					["StreamIndex"] = i,
					["CodecType"] = codecpar->codec_type.ToString(),
					["CodecId"] = codecpar->codec_id.ToString(),
					["TimeBase"] = new
					{
						Num = ptr2->time_base.num,
						Den = ptr2->time_base.den
					},
					["StartTime"] = ((ptr2->start_time != ffmpeg.AV_NOPTS_VALUE) ? ((double)(ptr2->start_time * ptr2->time_base.num) / (double)ptr2->time_base.den) : 0.0),
					["Duration"] = ((ptr2->duration != ffmpeg.AV_NOPTS_VALUE) ? ((double)(ptr2->duration * ptr2->time_base.num) / (double)ptr2->time_base.den) : 0.0),
					["NbFrames"] = ((ptr2->nb_frames > 0) ? ptr2->nb_frames : 0)
				};
				if (codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
				{
					dictionary3["Width"] = codecpar->width;
					dictionary3["Height"] = codecpar->height;
					dictionary3["PixelFormat"] = codecpar->format.ToString();
					dictionary3["BitRate"] = ((codecpar->bit_rate > 0) ? codecpar->bit_rate : 0);
					if (ptr2->avg_frame_rate.num > 0 && ptr2->avg_frame_rate.den > 0)
					{
						dictionary3["AvgFrameRate"] = (double)ptr2->avg_frame_rate.num / (double)ptr2->avg_frame_rate.den;
					}
					if (ptr2->r_frame_rate.num > 0 && ptr2->r_frame_rate.den > 0)
					{
						dictionary3["RealFrameRate"] = (double)ptr2->r_frame_rate.num / (double)ptr2->r_frame_rate.den;
					}
				}
				else if (codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
				{
					dictionary3["SampleRate"] = codecpar->sample_rate;
					dictionary3["Channels"] = codecpar->ch_layout.nb_channels;
					dictionary3["SampleFormat"] = codecpar->format.ToString();
					dictionary3["BitRate"] = ((codecpar->bit_rate > 0) ? codecpar->bit_rate : 0);
					dictionary3["FrameSize"] = ((codecpar->frame_size > 0) ? codecpar->frame_size : 0);
				}
				else if (codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_SUBTITLE)
				{
					dictionary3["SubtitleType"] = "Subtitle";
				}
				list.Add(dictionary3);
			}
			dictionary["AllStreams"] = list;
			_logger.Log("FFmpegMediaPlayer", "StreamInfoRead", dictionary);
			for (int j = 0; j < _formatContext->nb_streams; j++)
			{
				AVMediaType codec_type = _formatContext->streams[j]->codecpar->codec_type;
				if (codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && _videoStreamIndex < 0)
				{
					_videoStreamIndex = j;
					_logger.Log("FFmpegMediaPlayer", "VideoStreamFound", new
					{
						StreamIndex = j
					});
				}
				else if (codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO && _audioStreamIndex < 0)
				{
					_audioStreamIndex = j;
					_logger.Log("FFmpegMediaPlayer", "AudioStreamFound", new
					{
						StreamIndex = j
					});
				}
			}
			if (_videoStreamIndex < 0 && _audioStreamIndex < 0)
			{
				CloseInternal();
				return false;
			}
			if (_videoStreamIndex >= 0)
			{
				_logger.Log("FFmpegMediaPlayer", "InitializingVideoDecoder", new
				{
					StreamIndex = _videoStreamIndex
				});
				AVStream* ptr3 = _formatContext->streams[_videoStreamIndex];
				AVCodecParameters* codecpar2 = ptr3->codecpar;
				_logger.Log("FFmpegMediaPlayer", "VideoStreamInfo", new
				{
					StreamIndex = _videoStreamIndex,
					CodecId = codecpar2->codec_id.ToString(),
					Width = codecpar2->width,
					Height = codecpar2->height,
					PixelFormat = codecpar2->format.ToString(),
					BitRate = codecpar2->bit_rate,
					TimeBase = new
					{
						Num = ptr3->time_base.num,
						Den = ptr3->time_base.den
					},
					AvgFrameRate = ((ptr3->avg_frame_rate.num > 0 && ptr3->avg_frame_rate.den > 0) ? ((double)ptr3->avg_frame_rate.num / (double)ptr3->avg_frame_rate.den) : 0.0)
				});
				if (!InitializeVideoDecoder())
				{
					_logger.Log("FFmpegMediaPlayer", "VideoDecoderInitFailed", new
					{
						StreamIndex = _videoStreamIndex
					});
					_videoStreamIndex = -1;
				}
				else
				{
					_videoTimeBase = ptr3->time_base;
					_logger.Log("FFmpegMediaPlayer", "VideoDecoderInitialized", new
					{
						StreamIndex = _videoStreamIndex,
						Width = _videoWidth,
						Height = _videoHeight,
						FrameRate = _frameRate,
						TimeBase = new
						{
							Num = _videoTimeBase.num,
							Den = _videoTimeBase.den
						}
					});
				}
			}
			if (_audioStreamIndex >= 0)
			{
				_logger.Log("FFmpegMediaPlayer", "InitializingAudioDecoder", new
				{
					StreamIndex = _audioStreamIndex
				});
				AVStream* ptr4 = _formatContext->streams[_audioStreamIndex];
				AVCodecParameters* codecpar3 = ptr4->codecpar;
				_logger.Log("FFmpegMediaPlayer", "AudioStreamInfo", new
				{
					StreamIndex = _audioStreamIndex,
					CodecId = codecpar3->codec_id.ToString(),
					SampleRate = codecpar3->sample_rate,
					Channels = codecpar3->ch_layout.nb_channels,
					SampleFormat = codecpar3->format.ToString(),
					BitRate = codecpar3->bit_rate,
					TimeBase = new
					{
						Num = ptr4->time_base.num,
						Den = ptr4->time_base.den
					}
				});
				if (!InitializeAudioDecoder())
				{
					_logger.Log("FFmpegMediaPlayer", "AudioDecoderInitFailed", new
					{
						StreamIndex = _audioStreamIndex
					});
					_audioStreamIndex = -1;
				}
				else
				{
					_audioTimeBase = ptr4->time_base;
					_logger.Log("FFmpegMediaPlayer", "AudioDecoderInitialized", new
					{
						StreamIndex = _audioStreamIndex,
						SampleRate = _audioCodecContext->sample_rate,
						Channels = _audioCodecContext->ch_layout.nb_channels,
						TimeBase = new
						{
							Num = _audioTimeBase.num,
							Den = _audioTimeBase.den
						}
					});
				}
			}
			if (_videoStreamIndex < 0 && _audioStreamIndex < 0)
			{
				CloseInternal();
				return false;
			}
			if (_formatContext->duration != ffmpeg.AV_NOPTS_VALUE)
			{
				_duration = (double)_formatContext->duration / 1000000.0;
				this.LengthChanged?.Invoke(this, new LengthChangedEventArgs((long)(_duration * 1000.0)));
			}
			_packet = ffmpeg.av_packet_alloc();
			if (_packet == null)
			{
				_logger.Log("FFmpegMediaPlayer", "PacketAllocationFailed");
				CloseInternal();
				return false;
			}
			_pendingFrameCount = 0;
			_droppedFrames = 0;
			_startTime = 0.0;
			_playbackStartWallTime = 0.0;
			_audioStartPts = 0.0;
			_needsResync = true;
			_audioClock = 0.0;
			_videoClock = 0.0;
			_totalPauseTime = 0.0;
			_pauseStartTime = 0.0;
			_seekTargetPts = -1.0;
			_logger.Log("FFmpegMediaPlayer", "MovieLoadingCompleted", new
			{
				Path = path,
				Duration = _duration,
				VideoStreamIndex = _videoStreamIndex,
				AudioStreamIndex = _audioStreamIndex,
				VideoWidth = _videoWidth,
				VideoHeight = _videoHeight,
				FrameRate = _frameRate,
				Timestamp = DateTime.Now
			});
			return true;
		}
	}

	private unsafe bool InitializeVideoDecoder()
	{
		AVStream* ptr = _formatContext->streams[_videoStreamIndex];
		AVCodecParameters* codecpar = ptr->codecpar;
		AVCodec* ptr2 = ffmpeg.avcodec_find_decoder(codecpar->codec_id);
		if (ptr2 == null)
		{
			return false;
		}
		_videoCodecContext = ffmpeg.avcodec_alloc_context3(ptr2);
		if (ffmpeg.avcodec_parameters_to_context(_videoCodecContext, codecpar) < 0)
		{
			return false;
		}
		_videoCodecContext->thread_count = Math.Max(1, Environment.ProcessorCount - 1);
		_videoCodecContext->thread_type = 3;
		if (ffmpeg.avcodec_open2(_videoCodecContext, ptr2, null) < 0)
		{
			return false;
		}
		_videoWidth = _videoCodecContext->width;
		_videoHeight = _videoCodecContext->height;
		AVRational avg_frame_rate = ptr->avg_frame_rate;
		_frameRate = ((avg_frame_rate.num > 0 && avg_frame_rate.den > 0) ? ((double)avg_frame_rate.num / (double)avg_frame_rate.den) : 30.0);
		_frame = ffmpeg.av_frame_alloc();
		_rgbFrame = ffmpeg.av_frame_alloc();
		_rgbBufferSize = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_BGRA, _videoWidth, _videoHeight, 1);
		_rgbBuffer = (byte*)ffmpeg.av_malloc((ulong)_rgbBufferSize);
		byte_ptrArray4 dst_data = default(byte_ptrArray4);
		int_array4 dst_linesize = default(int_array4);
		ffmpeg.av_image_fill_arrays(ref dst_data, ref dst_linesize, _rgbBuffer, AVPixelFormat.AV_PIX_FMT_BGRA, _videoWidth, _videoHeight, 1);
		_rgbFrame->data[0u] = dst_data[0u];
		_rgbFrame->data[1u] = dst_data[1u];
		_rgbFrame->data[2u] = dst_data[2u];
		_rgbFrame->data[3u] = dst_data[3u];
		_rgbFrame->linesize[0u] = dst_linesize[0u];
		_rgbFrame->linesize[1u] = dst_linesize[1u];
		_rgbFrame->linesize[2u] = dst_linesize[2u];
		_rgbFrame->linesize[3u] = dst_linesize[3u];
		_swsContext = ffmpeg.sws_getContext(_videoWidth, _videoHeight, _videoCodecContext->pix_fmt, _videoWidth, _videoHeight, AVPixelFormat.AV_PIX_FMT_BGRA, 2, null, null, null);
		return _swsContext != null;
	}

	private unsafe bool InitializeAudioDecoder()
	{
		AVCodecParameters* codecpar = _formatContext->streams[_audioStreamIndex]->codecpar;
		AVCodec* ptr = ffmpeg.avcodec_find_decoder(codecpar->codec_id);
		if (ptr == null)
		{
			return false;
		}
		_audioCodecContext = ffmpeg.avcodec_alloc_context3(ptr);
		if (ffmpeg.avcodec_parameters_to_context(_audioCodecContext, codecpar) < 0)
		{
			return false;
		}
		if (ffmpeg.avcodec_open2(_audioCodecContext, ptr, null) < 0)
		{
			return false;
		}
		try
		{
			int sample_rate = _audioCodecContext->sample_rate;
			_ = *_audioCodecContext;
			_swrContext = ffmpeg.swr_alloc();
			AVChannelLayout ch_layout = _audioCodecContext->ch_layout;
			ffmpeg.av_opt_set_chlayout(_swrContext, "in_chlayout", &ch_layout, 0);
			ffmpeg.av_opt_set_int(_swrContext, "in_sample_rate", sample_rate, 0);
			ffmpeg.av_opt_set_sample_fmt(_swrContext, "in_sample_fmt", _audioCodecContext->sample_fmt, 0);
			AVChannelLayout aVChannelLayout = default(AVChannelLayout);
			ffmpeg.av_channel_layout_default(&aVChannelLayout, 2);
			ffmpeg.av_opt_set_chlayout(_swrContext, "out_chlayout", &aVChannelLayout, 0);
			ffmpeg.av_opt_set_int(_swrContext, "out_sample_rate", sample_rate, 0);
			ffmpeg.av_opt_set_sample_fmt(_swrContext, "out_sample_fmt", AVSampleFormat.AV_SAMPLE_FMT_S16, 0);
			if (ffmpeg.swr_init(_swrContext) < 0)
			{
				SwrContext* swrContext = _swrContext;
				ffmpeg.swr_free(&swrContext);
				_swrContext = null;
			}
			if (_audioPlayerFactory != null)
			{
				AudioPlayer = _audioPlayerFactory(sample_rate, 2);
				_ = AudioPlayer;
			}
		}
		catch (Exception)
		{
			AudioPlayer = null;
		}
		return true;
	}

	public unsafe void Play()
	{
		lock (_lock)
		{
			if (_formatContext != null)
			{
				if (_isPaused)
				{
					_isPaused = false;
					_audioPlayer?.Resume();
					_logger.Log("FFmpegMediaPlayer", "Resume", new
					{
						Position = _position
					});
					this.Playing?.Invoke(this, EventArgs.Empty);
				}
				else if (!_isPlaying)
				{
					_isPlaying = true;
					_isPaused = false;
					_cancellationTokenSource = new CancellationTokenSource();
					SyncAudioPlayerState();
					_logger.Log("FFmpegMediaPlayer", "Play", new
					{
						Position = _position,
						Duration = _duration
					});
					_playbackThread = new Thread(PlaybackLoop)
					{
						Name = "FFmpegPlayback",
						IsBackground = true
					};
					_playbackThread.Start(_cancellationTokenSource.Token);
					this.Playing?.Invoke(this, EventArgs.Empty);
				}
			}
		}
	}

	public void Pause()
	{
		lock (_lock)
		{
			if (_isPlaying && !_isPaused)
			{
				_isPaused = true;
				_audioPlayer?.Pause();
				_logger.Log("FFmpegMediaPlayer", "Pause", new
				{
					Position = _position
				});
				this.Paused?.Invoke(this, EventArgs.Empty);
			}
		}
	}

	private void SyncAudioPlayerState()
	{
		if (_audioPlayer == null)
		{
			return;
		}
		try
		{
			_audioPlayer.Stop();
			Thread.Sleep(10);
			if (_isPlaying && !_isPaused)
			{
				_audioPlayer.Resume();
			}
		}
		catch (Exception ex)
		{
			_logger.Log("FFmpegMediaPlayer", "AudioPlayerSyncError", new
			{
				Error = ex.Message
			});
		}
	}

	public void ResetAudioPlayerState()
	{
		lock (_lock)
		{
			if (_audioPlayer == null)
			{
				return;
			}
			try
			{
				_audioPlayer.Stop();
				Thread.Sleep(20);
				_audioPlayer.SetVolume((float)_volume / 100f);
				if (_isPlaying && !_isPaused)
				{
					_audioPlayer.Resume();
				}
			}
			catch (Exception ex)
			{
				_logger.Log("FFmpegMediaPlayer", "AudioPlayerResetError", new
				{
					Error = ex.Message
				});
			}
		}
	}

	public unsafe void Stop()
	{
		lock (_lock)
		{
			if (_isPlaying || _isPaused)
			{
				_logger.Log("FFmpegMediaPlayer", "Stop", new
				{
					Position = _position
				});
				_cancellationTokenSource?.Cancel();
				_playbackThread?.Join(1000);
				_playbackThread = null;
				_cancellationTokenSource?.Dispose();
				_cancellationTokenSource = null;
				_isPlaying = false;
				_isPaused = false;
				_position = 0.0;
				_startTime = 0.0;
				_playbackStartWallTime = 0.0;
				_audioStartPts = 0.0;
				_needsResync = true;
				_audioClock = 0.0;
				_videoClock = 0.0;
				_totalPauseTime = 0.0;
				_pauseStartTime = 0.0;
				_seekTargetPts = -1.0;
				if (_formatContext != null)
				{
					ffmpeg.av_seek_frame(_formatContext, -1, 0L, 1);
				}
				_audioPlayer?.Stop();
				this.Stopped?.Invoke(this, EventArgs.Empty);
			}
		}
	}

	public unsafe void Seek(float positionPercent)
	{
		lock (_lock)
		{
			if (_formatContext != null)
			{
				double position = _position;
				double num = _duration * (double)positionPercent;
				long timestamp = (long)(num * 1000000.0);
				ffmpeg.av_seek_frame(_formatContext, -1, timestamp, 1);
				if (_videoCodecContext != null)
				{
					ffmpeg.avcodec_flush_buffers(_videoCodecContext);
				}
				if (_audioCodecContext != null)
				{
					ffmpeg.avcodec_flush_buffers(_audioCodecContext);
				}
				_startTime = 0.0;
				_playbackStartWallTime = 0.0;
				_audioStartPts = 0.0;
				_needsResync = true;
				_audioClock = 0.0;
				_videoClock = 0.0;
				_totalPauseTime = 0.0;
				_pauseStartTime = 0.0;
				_position = num;
				_seekTargetPts = num;
				_audioPlayer?.Stop();
				_logger.Log("FFmpegMediaPlayer", "Seek", new
				{
					PositionPercent = positionPercent,
					OldPosition = position,
					NewPosition = _position,
					TargetTime = num
				});
			}
		}
	}

	private void CacheFrame(byte[] frameData, int width, int height, int stride, int dataLength, double pts)
	{
		lock (_lock)
		{
			byte[] array = new byte[dataLength];
			Array.Copy(frameData, array, dataLength);
			_frameCache.Enqueue(new CachedFrame(array, width, height, stride, dataLength, pts));
			while (_frameCache.Count > 30)
			{
				_frameCache.Dequeue();
			}
		}
	}

	public unsafe bool ShowFrameAtCurrentPosition()
	{
		lock (_lock)
		{
			if (_formatContext == null || _videoCodecContext == null || _packet == null)
			{
				return false;
			}
			if (_isPlaying && !_isPaused)
			{
				return false;
			}
			_logger.Log("FFmpegMediaPlayer", "ShowFrameAtCurrentPosition", new
			{
				Position = _position,
				IsPaused = _isPaused,
				IsPlaying = _isPlaying
			});
			return DecodeSingleFrame();
		}
	}

	public unsafe bool StepForward()
	{
		lock (_lock)
		{
			if (_formatContext == null || _videoCodecContext == null || _packet == null)
			{
				return false;
			}
			_stepMode = true;
			if (_isPaused && _isPlaying)
			{
				_isPaused = false;
				return true;
			}
			if (!_isPlaying)
			{
				return DecodeSingleFrame();
			}
			return true;
		}
	}

	public unsafe bool DecodeFirstFrame()
	{
		lock (_lock)
		{
			if (_formatContext == null || _videoCodecContext == null || _packet == null)
			{
				return false;
			}
			if (_isPlaying)
			{
				return false;
			}
			_logger.Log("FFmpegMediaPlayer", "DecodeFirstFrame");
			for (int i = 0; i < 200; i++)
			{
				if (ffmpeg.av_read_frame(_formatContext, _packet) < 0)
				{
					break;
				}
				if (_packet->stream_index == _videoStreamIndex && ffmpeg.avcodec_send_packet(_videoCodecContext, _packet) >= 0 && ffmpeg.avcodec_receive_frame(_videoCodecContext, _frame) >= 0)
				{
					ProcessSingleFrame();
					ffmpeg.av_packet_unref(_packet);
					ffmpeg.av_seek_frame(_formatContext, -1, 0L, 1);
					ffmpeg.avcodec_flush_buffers(_videoCodecContext);
					if (_audioCodecContext != null)
					{
						ffmpeg.avcodec_flush_buffers(_audioCodecContext);
					}
					_position = 0.0;
					_videoClock = 0.0;
					_audioClock = 0.0;
					_lastFramePts = -1.0;
					_needsResync = true;
					_seekTargetPts = -1.0;
					_logger.Log("FFmpegMediaPlayer", "FirstFrameDecoded", new
					{
						Width = _videoWidth,
						Height = _videoHeight
					});
					return true;
				}
				ffmpeg.av_packet_unref(_packet);
			}
			_logger.Log("FFmpegMediaPlayer", "FirstFrameDecodeFailed");
			return false;
		}
	}

	private unsafe bool DecodeSingleFrame()
	{
		if (_formatContext == null || _videoCodecContext == null || _packet == null)
		{
			return false;
		}
		for (int i = 0; i < 200; i++)
		{
			if (ffmpeg.av_read_frame(_formatContext, _packet) < 0)
			{
				return false;
			}
			if (_packet->stream_index == _videoStreamIndex && ffmpeg.avcodec_send_packet(_videoCodecContext, _packet) >= 0 && ffmpeg.avcodec_receive_frame(_videoCodecContext, _frame) >= 0)
			{
				if (_seekTargetPts >= 0.0)
				{
					long num = ((_frame->pts != ffmpeg.AV_NOPTS_VALUE) ? _frame->pts : _frame->best_effort_timestamp);
					if (num != ffmpeg.AV_NOPTS_VALUE && (double)(num * _videoTimeBase.num) / (double)_videoTimeBase.den + 0.001 < _seekTargetPts)
					{
						ffmpeg.av_packet_unref(_packet);
						continue;
					}
					_seekTargetPts = -1.0;
				}
				ProcessSingleFrame();
				ffmpeg.av_packet_unref(_packet);
				return true;
			}
			ffmpeg.av_packet_unref(_packet);
		}
		return false;
	}

	private unsafe void ProcessSingleFrame()
	{
		if (_frame == null || _rgbFrame == null || _swsContext == null)
		{
			return;
		}
		long num = ((_frame->pts != ffmpeg.AV_NOPTS_VALUE) ? _frame->pts : _frame->best_effort_timestamp);
		if (num == ffmpeg.AV_NOPTS_VALUE)
		{
			return;
		}
		double pts = (_lastFramePts = (_position = (_videoClock = (double)(num * _videoTimeBase.num) / (double)_videoTimeBase.den)));
		ffmpeg.sws_scale(_swsContext, _frame->data, _frame->linesize, 0, _videoHeight, _rgbFrame->data, _rgbFrame->linesize);
		int stride = _rgbFrame->linesize[0u];
		int videoWidth = _videoWidth;
		int videoHeight = _videoHeight;
		int rgbBufferSize = _rgbBufferSize;
		byte[] array;
		try
		{
			array = ArrayPool<byte>.Shared.Rent(rgbBufferSize);
		}
		catch (Exception)
		{
			return;
		}
		try
		{
			Marshal.Copy((nint)_rgbFrame->data[0u], array, 0, rgbBufferSize);
		}
		catch (Exception)
		{
			ArrayPool<byte>.Shared.Return(array);
			return;
		}
		CacheFrame(array, videoWidth, videoHeight, stride, rgbBufferSize, pts);
		FrameEventArgs eventArgs = new FrameEventArgs(array, videoWidth, videoHeight, stride, rgbBufferSize, pooled: true, delegate(byte[] buffer)
		{
			ArrayPool<byte>.Shared.Return(buffer);
		});
		if (_synchronizationCallback != null)
		{
			_synchronizationCallback(delegate
			{
				this.FrameReady?.Invoke(this, eventArgs);
				this.PositionChanged?.Invoke(this, new PositionChangedEventArgs(Position));
			});
		}
		else
		{
			this.FrameReady?.Invoke(this, eventArgs);
			this.PositionChanged?.Invoke(this, new PositionChangedEventArgs(Position));
		}
	}

	public unsafe bool StepBackward()
	{
		lock (_lock)
		{
			if (_formatContext == null || _videoCodecContext == null)
			{
				return false;
			}
			CachedFrame cachedFrame = null;
			if (_frameCache.Count > 1)
			{
				_frameCache.Dequeue();
				if (_frameCache.Count > 0)
				{
					cachedFrame = _frameCache.Peek();
				}
			}
			if (cachedFrame != null)
			{
				byte[] array = new byte[cachedFrame.DataLength];
				Array.Copy(cachedFrame.Data, array, cachedFrame.DataLength);
				FrameEventArgs eventArgs = new FrameEventArgs(array, cachedFrame.Width, cachedFrame.Height, cachedFrame.Stride, cachedFrame.DataLength, pooled: false, null);
				_position = cachedFrame.Pts;
				_videoClock = cachedFrame.Pts;
				_lastFramePts = cachedFrame.Pts;
				if (_synchronizationCallback != null)
				{
					_synchronizationCallback(delegate
					{
						this.FrameReady?.Invoke(this, eventArgs);
					});
				}
				else
				{
					this.FrameReady?.Invoke(this, eventArgs);
				}
				if (_synchronizationCallback != null)
				{
					_synchronizationCallback(delegate
					{
						this.PositionChanged?.Invoke(this, new PositionChangedEventArgs(Position));
					});
				}
				else
				{
					this.PositionChanged?.Invoke(this, new PositionChangedEventArgs(Position));
				}
				_frameCache.Clear();
				return true;
			}
			long num = (long)((_lastFramePts - 1.0 / _frameRate) * 1000000.0);
			if (num < 0)
			{
				num = 0L;
			}
			if (ffmpeg.av_seek_frame(_formatContext, _videoStreamIndex, num, 1) < 0)
			{
				return false;
			}
			if (_videoCodecContext != null)
			{
				ffmpeg.avcodec_flush_buffers(_videoCodecContext);
			}
			if (_audioCodecContext != null)
			{
				ffmpeg.avcodec_flush_buffers(_audioCodecContext);
			}
			_frameCache.Clear();
			_startTime = 0.0;
			_playbackStartWallTime = 0.0;
			_audioStartPts = 0.0;
			_needsResync = true;
			_audioClock = 0.0;
			_videoClock = 0.0;
			_seekTargetPts = -1.0;
			_audioPlayer?.Stop();
			double num2 = (double)num / 1000000.0;
			bool flag = false;
			for (int num3 = 0; num3 < 100; num3++)
			{
				if (!DecodeSingleFrame())
				{
					break;
				}
				flag = true;
				if (_lastFramePts >= num2 - 0.5 / _frameRate)
				{
					break;
				}
			}
			if (!flag)
			{
				_position = num2;
				_videoClock = num2;
				_lastFramePts = num2;
			}
			_stepMode = true;
			_isPaused = true;
			return flag;
		}
	}

	private unsafe void PlaybackLoop(object? state)
	{
		CancellationToken cancellationToken = (CancellationToken)state;
		Stopwatch stopwatch = Stopwatch.StartNew();
		double firstFramePts = 0.0;
		double lastVideoPts = 0.0;
		double lastAudioPts = 0.0;
		_totalPauseTime = 0.0;
		_pauseStartTime = 0.0;
		int num = 0;
		int num2 = 0;
		_logger.Log("FFmpegMediaPlayer", "PlaybackLoopStarted");
		bool flag = false;
		while (!cancellationToken.IsCancellationRequested && !flag)
		{
			if (_isPaused)
			{
				if (_pauseStartTime == 0.0)
				{
					_pauseStartTime = stopwatch.Elapsed.TotalSeconds;
				}
				Thread.Sleep(10);
				continue;
			}
			if (_pauseStartTime > 0.0)
			{
				_totalPauseTime += stopwatch.Elapsed.TotalSeconds - _pauseStartTime;
				_pauseStartTime = 0.0;
			}
			int num3 = 0;
			while (num3 < 15 && !cancellationToken.IsCancellationRequested && !flag)
			{
				int num4;
				int stream_index;
				lock (_lock)
				{
					if (_formatContext == null || _packet == null)
					{
						break;
					}
					num4 = ffmpeg.av_read_frame(_formatContext, _packet);
					stream_index = _packet->stream_index;
					goto IL_014e;
				}
				IL_014e:
				if (num4 < 0)
				{
					flag = true;
					_logger.Log("FFmpegMediaPlayer", "EndOfFile", new
					{
						ReadResult = num4
					});
					break;
				}
				try
				{
					if (stream_index == _audioStreamIndex)
					{
						lock (_lock)
						{
							if (_audioCodecContext != null)
							{
								ProcessAudioPacket(stopwatch, firstFramePts, ref lastAudioPts);
								num++;
							}
						}
					}
					else if (stream_index == _videoStreamIndex && _videoCodecContext != null)
					{
						ProcessVideoPacket(stopwatch, ref firstFramePts, ref lastVideoPts);
						num2++;
					}
				}
				finally
				{
					lock (_lock)
					{
						if (_packet != null)
						{
							ffmpeg.av_packet_unref(_packet);
						}
					}
				}
				num3++;
			}
			if (num3 > 0 && !flag)
			{
				Thread.Sleep(1);
			}
		}
		lock (_lock)
		{
			_logger.Log("FFmpegMediaPlayer", "PlaybackLoopEnded", new
			{
				AudioPacketsProcessed = num,
				VideoPacketsProcessed = num2
			});
			_isPlaying = false;
			_isPaused = false;
			if (_formatContext != null)
			{
				ffmpeg.av_seek_frame(_formatContext, -1, 0L, 1);
				if (_videoCodecContext != null)
				{
					ffmpeg.avcodec_flush_buffers(_videoCodecContext);
				}
				if (_audioCodecContext != null)
				{
					ffmpeg.avcodec_flush_buffers(_audioCodecContext);
				}
			}
			_position = 0.0;
			_needsResync = true;
			_lastFramePts = -1.0;
			_seekTargetPts = -1.0;
			_audioPlayer?.Stop();
		}
		if (_synchronizationCallback != null)
		{
			_synchronizationCallback(delegate
			{
				this.EndReached?.Invoke(this, EventArgs.Empty);
			});
		}
		else
		{
			this.EndReached?.Invoke(this, EventArgs.Empty);
		}
	}

	private unsafe void ProcessVideoPacket(Stopwatch stopwatch, ref double firstFramePts, ref double lastVideoPts)
	{
		lock (_lock)
		{
			if (ffmpeg.avcodec_send_packet(_videoCodecContext, _packet) < 0)
			{
				return;
			}
		}
		while (true)
		{
			FrameEventArgs eventArgs = null;
			bool flag = false;
			int num = 0;
			float positionSnapshot;
			lock (_lock)
			{
				if (ffmpeg.avcodec_receive_frame(_videoCodecContext, _frame) < 0)
				{
					break;
				}
				long num2 = ((_frame->pts != ffmpeg.AV_NOPTS_VALUE) ? _frame->pts : _frame->best_effort_timestamp);
				double num3;
				if (num2 != ffmpeg.AV_NOPTS_VALUE)
				{
					num3 = (double)(num2 * _videoTimeBase.num) / (double)_videoTimeBase.den;
					if (!(_seekTargetPts >= 0.0))
					{
						goto IL_0107;
					}
					if (!(num3 + 0.001 < _seekTargetPts))
					{
						_seekTargetPts = -1.0;
						goto IL_0107;
					}
				}
				goto end_IL_0059;
				IL_0107:
				if (_needsResync)
				{
					_needsResync = false;
					firstFramePts = 0.0;
					_logger.Log("FFmpegMediaPlayer", "Resync", new
					{
						FrameTime = num3
					});
				}
				if (firstFramePts == 0.0)
				{
					firstFramePts = num3;
					lastVideoPts = num3;
					_startTime = num3;
					_playbackStartWallTime = stopwatch.Elapsed.TotalSeconds;
					_videoClock = num3;
					_audioClock = num3;
					_logger.Log("FFmpegMediaPlayer", "FirstVideoFrame", new
					{
						FrameTime = num3,
						PTS = num2
					});
				}
				else
				{
					double num4 = num3 - lastVideoPts;
					if (Math.Abs(num4) > 1.0)
					{
						firstFramePts = num3;
						lastVideoPts = num3;
						_startTime = num3;
						_playbackStartWallTime = stopwatch.Elapsed.TotalSeconds;
					}
					else if (num4 > 0.002)
					{
						double num5 = stopwatch.Elapsed.TotalSeconds - _playbackStartWallTime - _totalPauseTime;
						num = (int)((num3 - _startTime - num5) * 1000.0);
						flag = num > 1 && num < 1000;
					}
					lastVideoPts = num3;
				}
				ffmpeg.sws_scale(_swsContext, _frame->data, _frame->linesize, 0, _videoHeight, _rgbFrame->data, _rgbFrame->linesize);
				_videoClock = num3;
				_position = num3;
				_lastFramePts = num3;
				if (_stepMode)
				{
					_isPaused = true;
					_audioPlayer?.Pause();
				}
				if (Interlocked.Increment(ref _pendingFrameCount) > 4)
				{
					Interlocked.Decrement(ref _pendingFrameCount);
					Interlocked.Increment(ref _droppedFrames);
					continue;
				}
				int stride = _rgbFrame->linesize[0u];
				int videoWidth = _videoWidth;
				int videoHeight = _videoHeight;
				int rgbBufferSize = _rgbBufferSize;
				positionSnapshot = Position;
				byte[] array = null;
				try
				{
					array = ArrayPool<byte>.Shared.Rent(rgbBufferSize);
					Marshal.Copy((nint)_rgbFrame->data[0u], array, 0, rgbBufferSize);
				}
				catch
				{
					if (array != null)
					{
						ArrayPool<byte>.Shared.Return(array);
					}
					Interlocked.Decrement(ref _pendingFrameCount);
					goto end_IL_0059;
				}
				CacheFrame(array, videoWidth, videoHeight, stride, rgbBufferSize, num3);
				eventArgs = new FrameEventArgs(array, videoWidth, videoHeight, stride, rgbBufferSize, pooled: true, delegate(byte[] buffer)
				{
					ArrayPool<byte>.Shared.Return(buffer);
				});
				goto IL_03be;
				end_IL_0059:;
			}
			continue;
			IL_03be:
			if (flag)
			{
				Thread.Sleep(num);
			}
			if (eventArgs == null)
			{
				continue;
			}
			if (_synchronizationCallback != null)
			{
				_synchronizationCallback(delegate
				{
					this.PositionChanged?.Invoke(this, new PositionChangedEventArgs(positionSnapshot));
				});
				_synchronizationCallback(delegate
				{
					try
					{
						this.FrameReady?.Invoke(this, eventArgs);
					}
					finally
					{
						Interlocked.Decrement(ref _pendingFrameCount);
					}
				});
			}
			else
			{
				this.PositionChanged?.Invoke(this, new PositionChangedEventArgs(positionSnapshot));
				try
				{
					this.FrameReady?.Invoke(this, eventArgs);
				}
				finally
				{
					Interlocked.Decrement(ref _pendingFrameCount);
				}
			}
		}
	}

	private unsafe void ProcessAudioPacket(Stopwatch stopwatch, double firstFramePts, ref double lastAudioPts)
	{
		if (_audioPlayer == null)
		{
			_logger.Log("FFmpegMediaPlayer", "ProcessAudioPacketFailed", new
			{
				Reason = "AudioPlayer is null"
			});
			return;
		}
		if (ffmpeg.avcodec_send_packet(_audioCodecContext, _packet) < 0)
		{
			_logger.Log("FFmpegMediaPlayer", "ProcessAudioPacketFailed", new
			{
				Reason = "avcodec_send_packet failed"
			});
			return;
		}
		AVFrame* ptr = ffmpeg.av_frame_alloc();
		byte** ptr2 = stackalloc byte*[1];
		try
		{
			while (ffmpeg.avcodec_receive_frame(_audioCodecContext, ptr) >= 0)
			{
				int nb_samples = ptr->nb_samples;
				int nb_channels = _audioCodecContext->ch_layout.nb_channels;
				int sample_rate = _audioCodecContext->sample_rate;
				long num = ((ptr->pts != ffmpeg.AV_NOPTS_VALUE) ? ptr->pts : ptr->best_effort_timestamp);
				if (num != ffmpeg.AV_NOPTS_VALUE)
				{
					double num2 = (double)(num * _audioTimeBase.num) / (double)_audioTimeBase.den;
					if (_seekTargetPts >= 0.0 && num2 + 0.001 < _seekTargetPts)
					{
						continue;
					}
					_audioClock = num2;
					if (lastAudioPts == 0.0 || _needsResync)
					{
						lastAudioPts = num2;
						_audioStartPts = num2;
						_needsResync = false;
						_logger.Log("FFmpegMediaPlayer", "FirstAudioFrame", new
						{
							AudioTime = num2,
							PTS = num,
							Samples = nb_samples,
							SampleRate = sample_rate,
							Channels = nb_channels
						});
					}
				}
				else
				{
					_logger.Log("FFmpegMediaPlayer", "AudioFrameNoPTS", new
					{
						Samples = nb_samples
					});
				}
				if (_swrContext != null)
				{
					int num3 = (int)ffmpeg.swr_get_delay(_swrContext, sample_rate) + nb_samples;
					short[] array = new short[num3 * 2];
					fixed (short* ptr3 = array)
					{
						*ptr2 = (byte*)ptr3;
						int num4 = ffmpeg.swr_convert(_swrContext, ptr2, num3, ptr->extended_data, nb_samples);
						if (num4 > 0)
						{
							_audioPlayer.QueueSamplesS16(ptr3, num4 * 2);
							_logger.Log("FFmpegMediaPlayer", "AudioQueued", new
							{
								InputSamples = nb_samples,
								ConvertedSamples = num4,
								OutputSamples = num4 * 2,
								AudioTime = ((num != ffmpeg.AV_NOPTS_VALUE) ? ((double)(num * _audioTimeBase.num) / (double)_audioTimeBase.den) : 0.0),
								Format = "S16"
							});
						}
					}
				}
				else
				{
					float[] array2 = new float[nb_samples * 2];
					AVSampleFormat format = (AVSampleFormat)ptr->format;
					ConvertAudioSamples(ptr, array2, nb_samples, nb_channels, format);
					_audioPlayer.QueueSamples(array2);
					_logger.Log("FFmpegMediaPlayer", "AudioQueued", new
					{
						Samples = nb_samples,
						Channels = nb_channels,
						AudioTime = ((num != ffmpeg.AV_NOPTS_VALUE) ? ((double)(num * _audioTimeBase.num) / (double)_audioTimeBase.den) : 0.0),
						Format = format.ToString()
					});
				}
			}
		}
		finally
		{
			ffmpeg.av_frame_free(&ptr);
		}
	}

	private unsafe void ConvertAudioSamples(AVFrame* frame, float[] output, int samples, int channels, AVSampleFormat format)
	{
		int num = 0;
		for (int i = 0; i < samples; i++)
		{
			for (int j = 0; j < channels; j++)
			{
				float value = 0f;
				uint num2 = (uint)(i * channels + j);
				uint i2 = (uint)j;
				uint num3 = (uint)i;
				switch (format)
				{
				case AVSampleFormat.AV_SAMPLE_FMT_FLT:
					value = ((float*)frame->data[0u])[num2];
					break;
				case AVSampleFormat.AV_SAMPLE_FMT_FLTP:
					value = ((float*)frame->data[i2])[num3];
					break;
				case AVSampleFormat.AV_SAMPLE_FMT_S16:
					value = (float)((short*)frame->data[0u])[num2] / 32768f;
					break;
				case AVSampleFormat.AV_SAMPLE_FMT_S16P:
					value = (float)((short*)frame->data[i2])[num3] / 32768f;
					break;
				case AVSampleFormat.AV_SAMPLE_FMT_S32:
					value = (float)((int*)frame->data[0u])[num2] / 2.1474836E+09f;
					break;
				case AVSampleFormat.AV_SAMPLE_FMT_S32P:
					value = (float)((int*)frame->data[i2])[num3] / 2.1474836E+09f;
					break;
				default:
					if (frame->data[i2] != null)
					{
						value = ((float*)frame->data[i2])[num3];
					}
					break;
				}
				output[num++] = Math.Clamp(value, -1f, 1f);
			}
		}
	}

	private unsafe void CloseInternal()
	{
		if (_swsContext != null)
		{
			ffmpeg.sws_freeContext(_swsContext);
			_swsContext = null;
		}
		if (_swrContext != null)
		{
			SwrContext* swrContext = _swrContext;
			ffmpeg.swr_free(&swrContext);
			_swrContext = null;
		}
		if (_rgbBuffer != null)
		{
			ffmpeg.av_free(_rgbBuffer);
			_rgbBuffer = null;
		}
		if (_frame != null)
		{
			AVFrame* frame = _frame;
			ffmpeg.av_frame_free(&frame);
			_frame = null;
		}
		if (_rgbFrame != null)
		{
			AVFrame* rgbFrame = _rgbFrame;
			ffmpeg.av_frame_free(&rgbFrame);
			_rgbFrame = null;
		}
		if (_packet != null)
		{
			AVPacket* packet = _packet;
			ffmpeg.av_packet_free(&packet);
			_packet = null;
		}
		if (_videoCodecContext != null)
		{
			AVCodecContext* videoCodecContext = _videoCodecContext;
			ffmpeg.avcodec_free_context(&videoCodecContext);
			_videoCodecContext = null;
		}
		if (_audioCodecContext != null)
		{
			AVCodecContext* audioCodecContext = _audioCodecContext;
			ffmpeg.avcodec_free_context(&audioCodecContext);
			_audioCodecContext = null;
		}
		if (_formatContext != null)
		{
			AVFormatContext* formatContext = _formatContext;
			ffmpeg.avformat_close_input(&formatContext);
			_formatContext = null;
		}
		AudioPlayer = null;
		_videoStreamIndex = -1;
		_audioStreamIndex = -1;
		_position = 0.0;
		_duration = 0.0;
		_pendingFrameCount = 0;
		_droppedFrames = 0;
		_startTime = 0.0;
		_playbackStartWallTime = 0.0;
		_audioStartPts = 0.0;
		_needsResync = true;
		_audioClock = 0.0;
		_videoClock = 0.0;
		_totalPauseTime = 0.0;
		_pauseStartTime = 0.0;
		_seekTargetPts = -1.0;
	}

	public void Close()
	{
		Stop();
		lock (_lock)
		{
			CloseInternal();
		}
	}

	public void Dispose()
	{
		if (!_isDisposed)
		{
			_isDisposed = true;
			_logger.Log("FFmpegMediaPlayer", "Dispose");
			Close();
			_logger.Dispose();
		}
	}
}
public class PositionChangedEventArgs : EventArgs
{
	public float Position { get; }

	public PositionChangedEventArgs(float position)
	{
		Position = position;
	}
}
public class LengthChangedEventArgs : EventArgs
{
	public long Length { get; }

	public LengthChangedEventArgs(long length)
	{
		Length = length;
	}
}
public sealed class FrameEventArgs : EventArgs, IDisposable
{
	private readonly Action<byte[]>? _releaseAction;

	private bool _disposed;

	public byte[] Data { get; }

	public int Width { get; }

	public int Height { get; }

	public int Stride { get; }

	public int DataLength { get; }

	public FrameEventArgs(byte[] data, int width, int height, int stride)
		: this(data, width, height, stride, (data != null) ? data.Length : 0, pooled: false, null)
	{
	}

	internal FrameEventArgs(byte[] data, int width, int height, int stride, int dataLength, bool pooled, Action<byte[]>? releaseAction)
	{
		Data = data;
		Width = width;
		Height = height;
		Stride = stride;
		DataLength = dataLength;
		_releaseAction = (pooled ? releaseAction : null);
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_releaseAction?.Invoke(Data);
			_disposed = true;
			GC.SuppressFinalize(this);
		}
	}

	~FrameEventArgs()
	{
		Dispose();
	}
}
internal static class FFmpegPathResolver
{
	public static string? TryConfigureBundledFFmpeg()
	{
		string text = FindBundledPath();
		if (string.IsNullOrEmpty(text))
		{
			return null;
		}
		ConfigureNativeSearchPath(text);
		return text;
	}

	public static string? FindBundledPath()
	{
		string baseDirectory = AppContext.BaseDirectory;
		string runtimeIdentifier = GetRuntimeIdentifier();
		List<string> list = new List<string>
		{
			Path.Combine(baseDirectory, "runtimes", runtimeIdentifier, "native"),
			Path.Combine(baseDirectory, runtimeIdentifier),
			Path.Combine(baseDirectory, "native", runtimeIdentifier),
			Path.Combine(baseDirectory, "ffmpeg", runtimeIdentifier),
			Path.Combine(baseDirectory, "ffmpeg", "bin")
		};
		if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
		{
			list.Add(Path.Combine(baseDirectory, "runtimes", "osx-universal", "native"));
		}
		foreach (string item in list)
		{
			if (HasFFmpegLibrary(item))
			{
				return item;
			}
		}
		return null;
	}

	public static void ConfigureNativeSearchPath(string path)
	{
		if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
		{
			ffmpeg.RootPath = path;
			AddPathVariable("PATH", path);
			if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
			{
				AddPathVariable("DYLD_LIBRARY_PATH", path);
			}
			else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
			{
				AddPathVariable("LD_LIBRARY_PATH", path);
			}
			InitializeBindings();
		}
	}

	public static void InitializeBindings()
	{
		DynamicallyLoadedBindings.Initialize();
	}

	public static bool TryValidateBindings()
	{
		try
		{
			return ffmpeg.avcodec_version() != 0;
		}
		catch
		{
			return false;
		}
	}

	public static string GetRuntimeIdentifier()
	{
		string text = (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" : (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" : "unknown")));
		return text + "-" + RuntimeInformation.ProcessArchitecture switch
		{
			Architecture.X64 => "x64", 
			Architecture.X86 => "x86", 
			Architecture.Arm64 => "arm64", 
			Architecture.Arm => "arm", 
			_ => "x64", 
		};
	}

	public static bool HasFFmpegLibrary(string path)
	{
		if (!Directory.Exists(path))
		{
			return false;
		}
		string[] array = ((!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) ? ((!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) ? new string[3] { "libavcodec.so*", "libavformat.so*", "libavutil.so*" } : new string[3] { "libavcodec*.dylib", "libavformat*.dylib", "libavutil*.dylib" }) : new string[3] { "avcodec*.dll", "avformat*.dll", "avutil*.dll" });
		foreach (string searchPattern in array)
		{
			try
			{
				if (Directory.GetFiles(path, searchPattern).Length != 0)
				{
					return true;
				}
			}
			catch
			{
			}
		}
		return false;
	}

	private static void AddPathVariable(string variable, string path)
	{
		string text = Environment.GetEnvironmentVariable(variable) ?? string.Empty;
		if (!text.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Any((string p) => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
		{
			string value = (string.IsNullOrEmpty(text) ? path : $"{path}{Path.PathSeparator}{text}");
			Environment.SetEnvironmentVariable(variable, value);
		}
	}
}
public interface IAudioPlayer : IDisposable
{
	void SetVolume(float volume);

	void Resume();

	void Pause();

	void Stop();

	double GetPlaybackTime();

	unsafe void QueueSamplesS16(short* samples, int sampleCount);

	void QueueSamples(float[] samples);
}
public interface IVideoRenderer : IDisposable
{
	int Width { get; set; }

	int Height { get; set; }

	void RenderFrame(nint frameData, int width, int height, int stride);

	void RenderFrame(byte[] frameData, int width, int height, int stride);

	void Clear();
}
public sealed class PlayerLogger : IDisposable
{
	private class LogEntry
	{
		public DateTime Timestamp { get; set; }

		public DateTime TimestampLocal { get; set; }

		public string Component { get; set; } = "";

		public string Operation { get; set; } = "";

		public object? Data { get; set; }
	}

	private readonly List<LogEntry> _logEntries = new List<LogEntry>();

	private readonly object _lock = new object();

	private readonly string _logFilePath;

	private bool _disposed;

	private DateTime _lastFlush = DateTime.Now;

	public PlayerLogger(string? logFilePath = null)
	{
		if (string.IsNullOrEmpty(logFilePath))
		{
			string text = Path.Combine(AppContext.BaseDirectory, "debug");
			Directory.CreateDirectory(text);
			_logFilePath = Path.Combine(text, $"player-log-{DateTime.Now:yyyyMMdd-HHmmss}.json");
		}
		else
		{
			_logFilePath = logFilePath;
		}
		Log("PlayerLogger", "Initialized", new
		{
			LogFile = _logFilePath
		});
	}

	public void Log(string component, string operation, object? data = null)
	{
		lock (_lock)
		{
			if (!_disposed)
			{
				LogEntry item = new LogEntry
				{
					Timestamp = DateTime.UtcNow,
					TimestampLocal = DateTime.Now,
					Component = component,
					Operation = operation,
					Data = data
				};
				_logEntries.Add(item);
				if (data != null)
				{
					_ = " | Data: " + JsonSerializer.Serialize(data);
				}
				if ((DateTime.Now - _lastFlush).TotalSeconds > 5.0)
				{
					Flush();
				}
			}
		}
	}

	public void Clear()
	{
		lock (_lock)
		{
			if (_disposed)
			{
				return;
			}
			_ = _logEntries.Count;
			_logEntries.Clear();
			try
			{
				JsonSerializerOptions options = new JsonSerializerOptions
				{
					WriteIndented = true,
					PropertyNamingPolicy = JsonNamingPolicy.CamelCase
				};
				string contents = JsonSerializer.Serialize(new List<LogEntry>(), options);
				File.WriteAllText(_logFilePath, contents, Encoding.UTF8);
				_lastFlush = DateTime.Now;
			}
			catch (Exception)
			{
			}
		}
	}

	public void Flush()
	{
		lock (_lock)
		{
			if (_disposed || _logEntries.Count == 0)
			{
				return;
			}
			try
			{
				JsonSerializerOptions options = new JsonSerializerOptions
				{
					WriteIndented = true,
					PropertyNamingPolicy = JsonNamingPolicy.CamelCase
				};
				string contents = JsonSerializer.Serialize(_logEntries, options);
				File.WriteAllText(_logFilePath, contents, Encoding.UTF8);
				_lastFlush = DateTime.Now;
			}
			catch (Exception)
			{
			}
		}
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			Flush();
		}
	}
}
