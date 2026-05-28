using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Permissions;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Data.Core;
using Avalonia.FFmpegVideoPlayer;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Templates;
using Avalonia.Markup.Xaml.XamlIl.Runtime;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using CompiledAvaloniaXaml;
using FFmpegVideoPlayer.Audio.OpenTK;
using FFmpegVideoPlayer.Core;

[assembly: CompilationRelaxations(8)]
[assembly: RuntimeCompatibility(WrapNonExceptionThrows = true)]
[assembly: Debuggable(DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints)]
[assembly: TargetFramework(".NETCoreApp,Version=v8.0", FrameworkDisplayName = ".NET 8.0")]
[assembly: AssemblyCompany("jojomondag")]
[assembly: AssemblyConfiguration("Release")]
[assembly: AssemblyDescription("FFmpeg video player for Avalonia UI. Windows FFmpeg binaries bundled; macOS and Linux users get auto-install via Homebrew/apt/dnf/pacman on first run.")]
[assembly: AssemblyFileVersion("2.8.0.0")]
[assembly: AssemblyInformationalVersion("2.8.0+651141a9263a3fdc2de2fde242f60de53b54b485")]
[assembly: AssemblyProduct("Avalonia.FFmpegVideoPlayer")]
[assembly: AssemblyTitle("Avalonia.FFmpegVideoPlayer")]
[assembly: AssemblyMetadata("RepositoryUrl", "https://github.com/jojomondag/FFmpegVideoPlayer.Avalonia")]
[assembly: AssemblyMetadata("AvaloniaUseCompiledBindingsByDefault", "False")]
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
[assembly: AssemblyVersion("2.8.0.0")]
[module: UnverifiableCode]
[module: RefSafetyRules(11)]
namespace Avalonia.FFmpegVideoPlayer
{
	/// <summary>
	/// CPU-based video renderer using WriteableBitmap.
	/// This is the default renderer for backward compatibility.
	/// </summary>
	public class CpuVideoRenderer : Control, IVideoRenderer, IDisposable
	{
		private WriteableBitmap? _bitmap;

		private int _width;

		private int _height;

		private Stretch _stretch = (Stretch)2;

		/// <summary>
		/// Gets or sets the video width.
		/// </summary>
		public int Width
		{
			get
			{
				return _width;
			}
			set
			{
				if (_width != value)
				{
					_width = value;
					UpdateBitmap();
				}
			}
		}

		/// <summary>
		/// Gets or sets the video height.
		/// </summary>
		public int Height
		{
			get
			{
				return _height;
			}
			set
			{
				if (_height != value)
				{
					_height = value;
					UpdateBitmap();
				}
			}
		}

		/// <summary>
		/// Gets or sets the stretch mode for video rendering.
		/// </summary>
		public Stretch Stretch
		{
			get
			{
				//IL_0001: Unknown result type (might be due to invalid IL or missing references)
				return _stretch;
			}
			set
			{
				//IL_0001: Unknown result type (might be due to invalid IL or missing references)
				//IL_0006: Unknown result type (might be due to invalid IL or missing references)
				//IL_000a: Unknown result type (might be due to invalid IL or missing references)
				//IL_000b: Unknown result type (might be due to invalid IL or missing references)
				if (_stretch != value)
				{
					_stretch = value;
					((Visual)this).InvalidateVisual();
				}
			}
		}

		public CpuVideoRenderer()
		{
			//IL_0002: Unknown result type (might be due to invalid IL or missing references)
			((Visual)this).ClipToBounds = true;
		}

		private void UpdateBitmap()
		{
			//IL_001f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0036: Unknown result type (might be due to invalid IL or missing references)
			//IL_003b: Unknown result type (might be due to invalid IL or missing references)
			//IL_004b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0055: Expected O, but got Unknown
			if (_width > 0 && _height > 0)
			{
				_bitmap = new WriteableBitmap(new PixelSize(_width, _height), new Vector(96.0, 96.0), (PixelFormat?)PixelFormat.Bgra8888, (AlphaFormat?)(AlphaFormat)0);
				((Layoutable)this).InvalidateMeasure();
				((Visual)this).InvalidateVisual();
			}
		}

		protected override Size MeasureOverride(Size availableSize)
		{
			//IL_0014: Unknown result type (might be due to invalid IL or missing references)
			//IL_001a: Unknown result type (might be due to invalid IL or missing references)
			//IL_0032: Unknown result type (might be due to invalid IL or missing references)
			//IL_0039: Unknown result type (might be due to invalid IL or missing references)
			//IL_00a5: Unknown result type (might be due to invalid IL or missing references)
			if (_width <= 0 || _height <= 0)
			{
				return default(Size);
			}
			Size result = default(Size);
			((Size)(ref result))..ctor((double)_width, (double)_height);
			if ((int)_stretch == 0)
			{
				return result;
			}
			double val = (double.IsInfinity(((Size)(ref availableSize)).Width) ? 1.0 : (((Size)(ref availableSize)).Width / (double)_width));
			double val2 = (double.IsInfinity(((Size)(ref availableSize)).Height) ? 1.0 : (((Size)(ref availableSize)).Height / (double)_height));
			double num = Math.Min(val, val2);
			return new Size((double)_width * num, (double)_height * num);
		}

		public unsafe void RenderFrame(nint frameData, int width, int height, int stride)
		{
			if (frameData == IntPtr.Zero || width <= 0 || height <= 0)
			{
				return;
			}
			if (_width != width || _height != height)
			{
				Width = width;
				Height = height;
			}
			if (_bitmap == null)
			{
				return;
			}
			try
			{
				ILockedFramebuffer val = _bitmap.Lock();
				try
				{
					nint address = val.Address;
					for (int i = 0; i < height; i++)
					{
						int num = i * stride;
						int num2 = i * val.RowBytes;
						int val2 = Math.Min(stride, val.RowBytes);
						int num3 = Math.Min(width * 4, val2);
						if (num3 > 0)
						{
							Buffer.MemoryCopy((void*)(frameData + num), (void*)(address + num2), num3, num3);
						}
					}
				}
				finally
				{
					((IDisposable)val)?.Dispose();
				}
				((Visual)this).InvalidateVisual();
			}
			catch (Exception)
			{
			}
		}

		public void RenderFrame(byte[] frameData, int width, int height, int stride)
		{
			if (frameData == null || frameData.Length == 0 || width <= 0 || height <= 0)
			{
				return;
			}
			if (_width != width || _height != height)
			{
				Width = width;
				Height = height;
			}
			if (_bitmap == null)
			{
				return;
			}
			try
			{
				ILockedFramebuffer val = _bitmap.Lock();
				try
				{
					nint address = val.Address;
					for (int i = 0; i < height; i++)
					{
						int num = i * stride;
						int num2 = i * val.RowBytes;
						int val2 = Math.Min(stride, val.RowBytes);
						int num3 = Math.Min(width * 4, val2);
						if (num3 > 0 && num + num3 <= frameData.Length)
						{
							Marshal.Copy(frameData, num, address + num2, num3);
						}
					}
				}
				finally
				{
					((IDisposable)val)?.Dispose();
				}
				((Visual)this).InvalidateVisual();
			}
			catch (Exception)
			{
			}
		}

		public unsafe void Clear()
		{
			if (_bitmap == null)
			{
				return;
			}
			ILockedFramebuffer val = _bitmap.Lock();
			try
			{
				byte* address = (byte*)val.Address;
				int num = val.RowBytes * Height;
				for (int i = 0; i < num; i++)
				{
					address[i] = 0;
				}
			}
			finally
			{
				((IDisposable)val)?.Dispose();
			}
			((Visual)this).InvalidateVisual();
		}

		public void Dispose()
		{
			_bitmap = null;
		}

		public override void Render(DrawingContext context)
		{
			//IL_0013: Unknown result type (might be due to invalid IL or missing references)
			//IL_0018: Unknown result type (might be due to invalid IL or missing references)
			//IL_002c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0031: Unknown result type (might be due to invalid IL or missing references)
			//IL_007d: Unknown result type (might be due to invalid IL or missing references)
			//IL_007f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0085: Unknown result type (might be due to invalid IL or missing references)
			//IL_008a: Unknown result type (might be due to invalid IL or missing references)
			//IL_008f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0097: Unknown result type (might be due to invalid IL or missing references)
			//IL_0098: Unknown result type (might be due to invalid IL or missing references)
			((Visual)this).Render(context);
			if (_bitmap == null)
			{
				return;
			}
			Rect bounds = ((Visual)this).Bounds;
			if (((Rect)(ref bounds)).Width > 0.0)
			{
				bounds = ((Visual)this).Bounds;
				if (((Rect)(ref bounds)).Height > 0.0 && _width > 0 && _height > 0)
				{
					Rect val = default(Rect);
					((Rect)(ref val))..ctor(0.0, 0.0, (double)_width, (double)_height);
					Rect val2 = CalculateDestinationRect(val, ((Visual)this).Bounds, _stretch);
					context.DrawImage((IImage)(object)_bitmap, val, val2);
				}
			}
		}

		private static Rect CalculateDestinationRect(Rect sourceRect, Rect bounds, Stretch stretch)
		{
			//IL_0020: Unknown result type (might be due to invalid IL or missing references)
			//IL_0036: Expected I4, but got Unknown
			//IL_008b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0090: Unknown result type (might be due to invalid IL or missing references)
			//IL_0096: Unknown result type (might be due to invalid IL or missing references)
			//IL_0097: Unknown result type (might be due to invalid IL or missing references)
			//IL_01b0: Unknown result type (might be due to invalid IL or missing references)
			//IL_011c: Unknown result type (might be due to invalid IL or missing references)
			//IL_00db: Unknown result type (might be due to invalid IL or missing references)
			//IL_01a6: Unknown result type (might be due to invalid IL or missing references)
			//IL_0165: Unknown result type (might be due to invalid IL or missing references)
			//IL_01ae: Unknown result type (might be due to invalid IL or missing references)
			//IL_01af: Unknown result type (might be due to invalid IL or missing references)
			//IL_0121: Unknown result type (might be due to invalid IL or missing references)
			//IL_01ab: Unknown result type (might be due to invalid IL or missing references)
			double num = ((Rect)(ref sourceRect)).Width / ((Rect)(ref sourceRect)).Height;
			double num2 = ((Rect)(ref bounds)).Width / ((Rect)(ref bounds)).Height;
			return (Rect)((int)stretch switch
			{
				0 => new Rect(((Rect)(ref bounds)).X + (((Rect)(ref bounds)).Width - ((Rect)(ref sourceRect)).Width) / 2.0, ((Rect)(ref bounds)).Y + (((Rect)(ref bounds)).Height - ((Rect)(ref sourceRect)).Height) / 2.0, ((Rect)(ref sourceRect)).Width, ((Rect)(ref sourceRect)).Height), 
				1 => bounds, 
				2 => (num > num2) ? new Rect(((Rect)(ref bounds)).X, ((Rect)(ref bounds)).Y + (((Rect)(ref bounds)).Height - ((Rect)(ref bounds)).Width / num) / 2.0, ((Rect)(ref bounds)).Width, ((Rect)(ref bounds)).Width / num) : new Rect(((Rect)(ref bounds)).X + (((Rect)(ref bounds)).Width - ((Rect)(ref bounds)).Height * num) / 2.0, ((Rect)(ref bounds)).Y, ((Rect)(ref bounds)).Height * num, ((Rect)(ref bounds)).Height), 
				3 => (num > num2) ? new Rect(((Rect)(ref bounds)).X + (((Rect)(ref bounds)).Width - ((Rect)(ref bounds)).Height * num) / 2.0, ((Rect)(ref bounds)).Y, ((Rect)(ref bounds)).Height * num, ((Rect)(ref bounds)).Height) : new Rect(((Rect)(ref bounds)).X, ((Rect)(ref bounds)).Y + (((Rect)(ref bounds)).Height - ((Rect)(ref bounds)).Width / num) / 2.0, ((Rect)(ref bounds)).Width, ((Rect)(ref bounds)).Width / num), 
				_ => bounds, 
			});
		}
	}
	/// <summary>
	/// OpenGL-based hardware-accelerated video renderer.
	/// Uses OpenGL textures to render video frames directly, eliminating CPU-&gt;GPU-&gt;CPU-&gt;GPU copies.
	///
	/// NOTE: This is a placeholder implementation. For full OpenGL support, use the optional
	/// FFmpegVideoPlayer.Rendering.OpenGL package which provides OpenTK-based rendering.
	/// </summary>
	public class OpenGLVideoRenderer : Control, IVideoRenderer, IDisposable
	{
		private int _videoWidth;

		private int _videoHeight;

		private Stretch _stretch = (Stretch)2;

		private WriteableBitmap? _fallbackBitmap;

		/// <summary>
		/// Gets or sets the video width.
		/// </summary>
		public int Width
		{
			get
			{
				return _videoWidth;
			}
			set
			{
				if (_videoWidth != value)
				{
					_videoWidth = value;
					UpdateFallbackBitmap();
				}
			}
		}

		/// <summary>
		/// Gets or sets the video height.
		/// </summary>
		public int Height
		{
			get
			{
				return _videoHeight;
			}
			set
			{
				if (_videoHeight != value)
				{
					_videoHeight = value;
					UpdateFallbackBitmap();
				}
			}
		}

		/// <summary>
		/// Gets or sets the stretch mode for video rendering.
		/// </summary>
		public Stretch Stretch
		{
			get
			{
				//IL_0001: Unknown result type (might be due to invalid IL or missing references)
				return _stretch;
			}
			set
			{
				//IL_0001: Unknown result type (might be due to invalid IL or missing references)
				//IL_0006: Unknown result type (might be due to invalid IL or missing references)
				//IL_000a: Unknown result type (might be due to invalid IL or missing references)
				//IL_000b: Unknown result type (might be due to invalid IL or missing references)
				if (_stretch != value)
				{
					_stretch = value;
					((Visual)this).InvalidateVisual();
				}
			}
		}

		public OpenGLVideoRenderer()
		{
			//IL_0002: Unknown result type (might be due to invalid IL or missing references)
			((Visual)this).ClipToBounds = true;
		}

		private void UpdateFallbackBitmap()
		{
			//IL_001f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0036: Unknown result type (might be due to invalid IL or missing references)
			//IL_003b: Unknown result type (might be due to invalid IL or missing references)
			//IL_004b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0055: Expected O, but got Unknown
			if (_videoWidth > 0 && _videoHeight > 0)
			{
				_fallbackBitmap = new WriteableBitmap(new PixelSize(_videoWidth, _videoHeight), new Vector(96.0, 96.0), (PixelFormat?)PixelFormat.Bgra8888, (AlphaFormat?)(AlphaFormat)0);
				((Visual)this).InvalidateVisual();
			}
		}

		public unsafe void RenderFrame(nint frameData, int width, int height, int stride)
		{
			if (frameData == IntPtr.Zero || width <= 0 || height <= 0)
			{
				return;
			}
			if (_videoWidth != width || _videoHeight != height)
			{
				Width = width;
				Height = height;
			}
			if (_fallbackBitmap == null)
			{
				return;
			}
			try
			{
				ILockedFramebuffer val = _fallbackBitmap.Lock();
				try
				{
					nint address = val.Address;
					for (int i = 0; i < height; i++)
					{
						int num = i * stride;
						int num2 = i * val.RowBytes;
						int val2 = Math.Min(stride, val.RowBytes);
						int num3 = Math.Min(width * 4, val2);
						if (num3 > 0)
						{
							Buffer.MemoryCopy((void*)(frameData + num), (void*)(address + num2), num3, num3);
						}
					}
				}
				finally
				{
					((IDisposable)val)?.Dispose();
				}
				((Visual)this).InvalidateVisual();
			}
			catch (Exception)
			{
			}
		}

		public unsafe void RenderFrame(byte[] frameData, int width, int height, int stride)
		{
			if (frameData != null && frameData.Length != 0 && width > 0 && height > 0)
			{
				fixed (byte* frameData2 = frameData)
				{
					RenderFrame((nint)frameData2, width, height, stride);
				}
			}
		}

		public unsafe void Clear()
		{
			if (_fallbackBitmap == null)
			{
				return;
			}
			ILockedFramebuffer val = _fallbackBitmap.Lock();
			try
			{
				byte* address = (byte*)val.Address;
				int num = val.RowBytes * Height;
				for (int i = 0; i < num; i++)
				{
					address[i] = 0;
				}
			}
			finally
			{
				((IDisposable)val)?.Dispose();
			}
			((Visual)this).InvalidateVisual();
		}

		public override void Render(DrawingContext context)
		{
			//IL_0013: Unknown result type (might be due to invalid IL or missing references)
			//IL_0018: Unknown result type (might be due to invalid IL or missing references)
			//IL_002c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0031: Unknown result type (might be due to invalid IL or missing references)
			//IL_007d: Unknown result type (might be due to invalid IL or missing references)
			//IL_007f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0085: Unknown result type (might be due to invalid IL or missing references)
			//IL_008a: Unknown result type (might be due to invalid IL or missing references)
			//IL_008f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0097: Unknown result type (might be due to invalid IL or missing references)
			//IL_0098: Unknown result type (might be due to invalid IL or missing references)
			((Visual)this).Render(context);
			if (_fallbackBitmap == null)
			{
				return;
			}
			Rect bounds = ((Visual)this).Bounds;
			if (((Rect)(ref bounds)).Width > 0.0)
			{
				bounds = ((Visual)this).Bounds;
				if (((Rect)(ref bounds)).Height > 0.0 && _videoWidth > 0 && _videoHeight > 0)
				{
					Rect val = default(Rect);
					((Rect)(ref val))..ctor(0.0, 0.0, (double)_videoWidth, (double)_videoHeight);
					Rect val2 = CalculateDestinationRect(val, ((Visual)this).Bounds, _stretch);
					context.DrawImage((IImage)(object)_fallbackBitmap, val, val2);
				}
			}
		}

		private static Rect CalculateDestinationRect(Rect sourceRect, Rect bounds, Stretch stretch)
		{
			//IL_0020: Unknown result type (might be due to invalid IL or missing references)
			//IL_0036: Expected I4, but got Unknown
			//IL_008b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0090: Unknown result type (might be due to invalid IL or missing references)
			//IL_0096: Unknown result type (might be due to invalid IL or missing references)
			//IL_0097: Unknown result type (might be due to invalid IL or missing references)
			//IL_01b0: Unknown result type (might be due to invalid IL or missing references)
			//IL_011c: Unknown result type (might be due to invalid IL or missing references)
			//IL_00db: Unknown result type (might be due to invalid IL or missing references)
			//IL_01a6: Unknown result type (might be due to invalid IL or missing references)
			//IL_0165: Unknown result type (might be due to invalid IL or missing references)
			//IL_01ae: Unknown result type (might be due to invalid IL or missing references)
			//IL_01af: Unknown result type (might be due to invalid IL or missing references)
			//IL_0121: Unknown result type (might be due to invalid IL or missing references)
			//IL_01ab: Unknown result type (might be due to invalid IL or missing references)
			double num = ((Rect)(ref sourceRect)).Width / ((Rect)(ref sourceRect)).Height;
			double num2 = ((Rect)(ref bounds)).Width / ((Rect)(ref bounds)).Height;
			return (Rect)((int)stretch switch
			{
				0 => new Rect(((Rect)(ref bounds)).X + (((Rect)(ref bounds)).Width - ((Rect)(ref sourceRect)).Width) / 2.0, ((Rect)(ref bounds)).Y + (((Rect)(ref bounds)).Height - ((Rect)(ref sourceRect)).Height) / 2.0, ((Rect)(ref sourceRect)).Width, ((Rect)(ref sourceRect)).Height), 
				1 => bounds, 
				2 => (num > num2) ? new Rect(((Rect)(ref bounds)).X, ((Rect)(ref bounds)).Y + (((Rect)(ref bounds)).Height - ((Rect)(ref bounds)).Width / num) / 2.0, ((Rect)(ref bounds)).Width, ((Rect)(ref bounds)).Width / num) : new Rect(((Rect)(ref bounds)).X + (((Rect)(ref bounds)).Width - ((Rect)(ref bounds)).Height * num) / 2.0, ((Rect)(ref bounds)).Y, ((Rect)(ref bounds)).Height * num, ((Rect)(ref bounds)).Height), 
				3 => (num > num2) ? new Rect(((Rect)(ref bounds)).X + (((Rect)(ref bounds)).Width - ((Rect)(ref bounds)).Height * num) / 2.0, ((Rect)(ref bounds)).Y, ((Rect)(ref bounds)).Height * num, ((Rect)(ref bounds)).Height) : new Rect(((Rect)(ref bounds)).X, ((Rect)(ref bounds)).Y + (((Rect)(ref bounds)).Height - ((Rect)(ref bounds)).Width / num) / 2.0, ((Rect)(ref bounds)).Width, ((Rect)(ref bounds)).Width / num), 
				_ => bounds, 
			});
		}

		public void Dispose()
		{
			_fallbackBitmap = null;
		}
	}
	/// <summary>
	/// Video rendering mode.
	/// </summary>
	public enum VideoRenderingMode
	{
		/// <summary>
		/// CPU-based rendering using WriteableBitmap (default, backward compatible).
		/// </summary>
		Cpu,
		/// <summary>
		/// Hardware-accelerated OpenGL rendering (requires OpenGL support).
		/// </summary>
		OpenGL
	}
	/// <summary>
	/// A self-contained video player control with playback controls, seek bar, and volume control.
	/// Uses FFmpeg for cross-platform media playback including ARM64 macOS.
	/// Requires FFmpeg 8.x libraries (libavcodec.62) to be available.
	/// </summary>
	public class VideoPlayerControl : UserControl
	{
		[CompilerGenerated]
		private class XamlClosure_1
		{
			public static object? Build_1(IServiceProvider? P_0)
			{
				//IL_0007: Unknown result type (might be due to invalid IL or missing references)
				//IL_000c: Unknown result type (might be due to invalid IL or missing references)
				//IL_001d: Expected O, but got Unknown
				XamlIlContext.Context<VideoPlayerControl> context = CreateContext(P_0);
				return (object?)new PathGeometry
				{
					Figures = PathFigures.Parse("M 6,4 L 6,16 L 18,10 Z")
				};
			}

			public static XamlIlContext.Context<VideoPlayerControl?>? CreateContext(IServiceProvider? P_0)
			{
				//IL_003d: Unknown result type (might be due to invalid IL or missing references)
				XamlIlContext.Context<VideoPlayerControl> context = new XamlIlContext.Context<VideoPlayerControl>(P_0, new object[1] { !AvaloniaResources.NamespaceInfo:/VideoPlayerControl.axaml.Singleton }, "avares://Avalonia.FFmpegVideoPlayer/VideoPlayerControl.axaml");
				if (P_0 != null)
				{
					object service = P_0.GetService(typeof(IRootObjectProvider));
					if (service != null)
					{
						service = ((IRootObjectProvider)service).RootObject;
						context.RootObject = (VideoPlayerControl)service;
					}
				}
				return context;
			}

			public static object? Build_2(IServiceProvider? P_0)
			{
				//IL_0007: Unknown result type (might be due to invalid IL or missing references)
				//IL_000c: Unknown result type (might be due to invalid IL or missing references)
				//IL_001d: Expected O, but got Unknown
				XamlIlContext.Context<VideoPlayerControl> context = CreateContext(P_0);
				return (object?)new PathGeometry
				{
					Figures = PathFigures.Parse("M 6,4 L 10,4 L 10,16 L 6,16 Z M 14,4 L 18,4 L 18,16 L 14,16 Z")
				};
			}

			public static object? Build_3(IServiceProvider? P_0)
			{
				//IL_0007: Unknown result type (might be due to invalid IL or missing references)
				//IL_000c: Unknown result type (might be due to invalid IL or missing references)
				//IL_001d: Expected O, but got Unknown
				XamlIlContext.Context<VideoPlayerControl> context = CreateContext(P_0);
				return (object?)new PathGeometry
				{
					Figures = PathFigures.Parse("M 6,6 L 18,6 L 18,18 L 6,18 Z")
				};
			}

			public static object? Build_4(IServiceProvider? P_0)
			{
				//IL_0007: Unknown result type (might be due to invalid IL or missing references)
				//IL_000c: Unknown result type (might be due to invalid IL or missing references)
				//IL_001d: Expected O, but got Unknown
				XamlIlContext.Context<VideoPlayerControl> context = CreateContext(P_0);
				return (object?)new PathGeometry
				{
					Figures = PathFigures.Parse("M 4,6 L 4,8 L 6,8 L 8,6 L 16,6 L 18,8 L 18,16 L 4,16 Z M 4,8 L 6,10 L 18,10 L 18,8 Z")
				};
			}

			public static object? Build_5(IServiceProvider? P_0)
			{
				//IL_0007: Unknown result type (might be due to invalid IL or missing references)
				//IL_000c: Unknown result type (might be due to invalid IL or missing references)
				//IL_001d: Expected O, but got Unknown
				XamlIlContext.Context<VideoPlayerControl> context = CreateContext(P_0);
				return (object?)new PathGeometry
				{
					Figures = PathFigures.Parse("M 5,6 L 5,14 L 9,14 L 13,18 L 13,2 L 9,6 Z M 15,8 A 2,2 0 0,1 15,12 M 17,6 A 3,3 0 0,1 17,14")
				};
			}

			public static object? Build_6(IServiceProvider? P_0)
			{
				//IL_0007: Unknown result type (might be due to invalid IL or missing references)
				//IL_000c: Unknown result type (might be due to invalid IL or missing references)
				//IL_001d: Expected O, but got Unknown
				XamlIlContext.Context<VideoPlayerControl> context = CreateContext(P_0);
				return (object?)new PathGeometry
				{
					Figures = PathFigures.Parse("M 5,6 L 5,14 L 9,14 L 13,18 L 13,2 L 9,6 Z M 15,5 L 19,9 M 15,9 L 19,5")
				};
			}

			public static object? Build_7(IServiceProvider? P_0)
			{
				//IL_0007: Unknown result type (might be due to invalid IL or missing references)
				//IL_000c: Unknown result type (might be due to invalid IL or missing references)
				//IL_001d: Expected O, but got Unknown
				XamlIlContext.Context<VideoPlayerControl> context = CreateContext(P_0);
				return (object?)new PathGeometry
				{
					Figures = PathFigures.Parse("M 5,5 L 5,10 L 7,10 L 7,7 L 10,7 L 10,5 Z M 14,5 L 14,7 L 17,7 L 17,10 L 19,10 L 19,5 Z M 7,14 L 5,14 L 5,19 L 10,19 L 10,17 L 7,17 Z M 17,17 L 14,17 L 14,19 L 19,19 L 19,14 L 17,14 Z")
				};
			}

			public static object? Build_8(IServiceProvider? P_0)
			{
				//IL_0007: Unknown result type (might be due to invalid IL or missing references)
				//IL_000c: Unknown result type (might be due to invalid IL or missing references)
				//IL_001d: Expected O, but got Unknown
				XamlIlContext.Context<VideoPlayerControl> context = CreateContext(P_0);
				return (object?)new PathGeometry
				{
					Figures = PathFigures.Parse("M 14,14 L 14,19 L 16,19 L 16,16 L 19,16 L 19,14 Z M 5,14 L 5,16 L 8,16 L 8,19 L 10,19 L 10,14 Z M 16,5 L 14,5 L 14,10 L 19,10 L 19,8 L 16,8 Z M 8,8 L 5,8 L 5,10 L 10,10 L 10,5 L 8,5 Z")
				};
			}

			public static object? Build_9(IServiceProvider? P_0)
			{
				//IL_0008: Unknown result type (might be due to invalid IL or missing references)
				//IL_0012: Expected O, but got Unknown
				//IL_002b: Unknown result type (might be due to invalid IL or missing references)
				//IL_0030: Unknown result type (might be due to invalid IL or missing references)
				//IL_0049: Unknown result type (might be due to invalid IL or missing references)
				//IL_004e: Unknown result type (might be due to invalid IL or missing references)
				//IL_0067: Unknown result type (might be due to invalid IL or missing references)
				//IL_006c: Unknown result type (might be due to invalid IL or missing references)
				//IL_007b: Unknown result type (might be due to invalid IL or missing references)
				//IL_0080: Unknown result type (might be due to invalid IL or missing references)
				//IL_0082: Expected O, but got Unknown
				//IL_0082: Unknown result type (might be due to invalid IL or missing references)
				//IL_0088: Expected O, but got Unknown
				//IL_008d: Expected O, but got Unknown
				//IL_0097: Unknown result type (might be due to invalid IL or missing references)
				//IL_009c: Unknown result type (might be due to invalid IL or missing references)
				XamlIlContext.Context<VideoPlayerControl> context = CreateContext(P_0);
				context.IntermediateRoot = (object)new Border();
				object obj = context.IntermediateRoot;
				((ISupportInitialize)obj).BeginInit();
				AvaloniaObjectExtensions.Bind((AvaloniaObject)obj, (AvaloniaProperty)(object)Border.BackgroundProperty, new TemplateBinding((AvaloniaProperty)(object)TemplatedControl.BackgroundProperty).ProvideValue(), (object)null);
				AvaloniaObjectExtensions.Bind((AvaloniaObject)obj, (AvaloniaProperty)(object)Border.CornerRadiusProperty, new TemplateBinding((AvaloniaProperty)(object)TemplatedControl.CornerRadiusProperty).ProvideValue(), (object)null);
				AvaloniaObjectExtensions.Bind((AvaloniaObject)obj, (AvaloniaProperty)(object)Decorator.PaddingProperty, new TemplateBinding((AvaloniaProperty)(object)TemplatedControl.PaddingProperty).ProvideValue(), (object)null);
				ContentPresenter val = new ContentPresenter();
				ContentPresenter val2 = val;
				((ISupportInitialize)val).BeginInit();
				((Decorator)obj).Child = (Control)val;
				XamlDynamicSetters.<>XamlDynamicSetter_1(val2, (BindingPriority)2, new TemplateBinding((AvaloniaProperty)(object)ContentControl.ContentProperty).ProvideValue());
				((AvaloniaObject)val2).SetValue<HorizontalAlignment>(Layoutable.HorizontalAlignmentProperty, (HorizontalAlignment)2, (BindingPriority)2);
				((AvaloniaObject)val2).SetValue<VerticalAlignment>(Layoutable.VerticalAlignmentProperty, (VerticalAlignment)2, (BindingPriority)2);
				((ISupportInitialize)val2).EndInit();
				((ISupportInitialize)obj).EndInit();
				return obj;
			}

			public static object? Build_10(IServiceProvider? P_0)
			{
				//IL_0008: Unknown result type (might be due to invalid IL or missing references)
				//IL_0012: Expected O, but got Unknown
				//IL_002b: Unknown result type (might be due to invalid IL or missing references)
				//IL_0030: Unknown result type (might be due to invalid IL or missing references)
				//IL_0049: Unknown result type (might be due to invalid IL or missing references)
				//IL_004e: Unknown result type (might be due to invalid IL or missing references)
				//IL_0067: Unknown result type (might be due to invalid IL or missing references)
				//IL_006c: Unknown result type (might be due to invalid IL or missing references)
				//IL_0085: Unknown result type (might be due to invalid IL or missing references)
				//IL_008a: Unknown result type (might be due to invalid IL or missing references)
				//IL_00a3: Unknown result type (might be due to invalid IL or missing references)
				//IL_00a8: Unknown result type (might be due to invalid IL or missing references)
				//IL_00b7: Unknown result type (might be due to invalid IL or missing references)
				//IL_00bc: Unknown result type (might be due to invalid IL or missing references)
				//IL_00be: Expected O, but got Unknown
				//IL_00be: Unknown result type (might be due to invalid IL or missing references)
				//IL_00c4: Expected O, but got Unknown
				//IL_00c9: Expected O, but got Unknown
				//IL_00d3: Unknown result type (might be due to invalid IL or missing references)
				//IL_00d8: Unknown result type (might be due to invalid IL or missing references)
				XamlIlContext.Context<VideoPlayerControl> context = CreateContext(P_0);
				context.IntermediateRoot = (object)new Border();
				object obj = context.IntermediateRoot;
				((ISupportInitialize)obj).BeginInit();
				AvaloniaObjectExtensions.Bind((AvaloniaObject)obj, (AvaloniaProperty)(object)Border.BackgroundProperty, new TemplateBinding((AvaloniaProperty)(object)TemplatedControl.BackgroundProperty).ProvideValue(), (object)null);
				AvaloniaObjectExtensions.Bind((AvaloniaObject)obj, (AvaloniaProperty)(object)Border.BorderBrushProperty, new TemplateBinding((AvaloniaProperty)(object)TemplatedControl.BorderBrushProperty).ProvideValue(), (object)null);
				AvaloniaObjectExtensions.Bind((AvaloniaObject)obj, (AvaloniaProperty)(object)Border.BorderThicknessProperty, new TemplateBinding((AvaloniaProperty)(object)TemplatedControl.BorderThicknessProperty).ProvideValue(), (object)null);
				AvaloniaObjectExtensions.Bind((AvaloniaObject)obj, (AvaloniaProperty)(object)Border.CornerRadiusProperty, new TemplateBinding((AvaloniaProperty)(object)TemplatedControl.CornerRadiusProperty).ProvideValue(), (object)null);
				AvaloniaObjectExtensions.Bind((AvaloniaObject)obj, (AvaloniaProperty)(object)Decorator.PaddingProperty, new TemplateBinding((AvaloniaProperty)(object)TemplatedControl.PaddingProperty).ProvideValue(), (object)null);
				ContentPresenter val = new ContentPresenter();
				ContentPresenter val2 = val;
				((ISupportInitialize)val).BeginInit();
				((Decorator)obj).Child = (Control)val;
				XamlDynamicSetters.<>XamlDynamicSetter_1(val2, (BindingPriority)2, new TemplateBinding((AvaloniaProperty)(object)ContentControl.ContentProperty).ProvideValue());
				((AvaloniaObject)val2).SetValue<HorizontalAlignment>(Layoutable.HorizontalAlignmentProperty, (HorizontalAlignment)2, (BindingPriority)2);
				((AvaloniaObject)val2).SetValue<VerticalAlignment>(Layoutable.VerticalAlignmentProperty, (VerticalAlignment)2, (BindingPriority)2);
				((ISupportInitialize)val2).EndInit();
				((ISupportInitialize)obj).EndInit();
				return obj;
			}
		}

		private FFmpegMediaPlayer? _mediaPlayer;

		private Slider? _seekBar;

		private Slider? _volumeSlider;

		private TextBlock? _currentTimeText;

		private TextBlock? _totalTimeText;

		private Path? _playPauseIcon;

		private TextBlock? _playPauseText;

		private Path? _volumeIcon;

		private bool _isDraggingSeekBar;

		private bool _isMuted;

		private int _previousVolume = 100;

		private bool _isInitialized;

		private Border? _controlPanelBorder;

		private Border? _videoBorder;

		private Button? _openButton;

		private IVideoRenderer? _videoRenderer;

		private string? _currentMediaPath;

		private bool _hasMediaLoaded;

		/// <summary>
		/// Defines the Volume property.
		/// </summary>
		public static readonly StyledProperty<int> VolumeProperty = AvaloniaProperty.Register<VideoPlayerControl, int>("Volume", 100, false, (BindingMode)1, (Func<int, bool>)null, (Func<AvaloniaObject, int, int>)null, false);

		/// <summary>
		/// Defines the AutoPlay property.
		/// </summary>
		public static readonly StyledProperty<bool> AutoPlayProperty = AvaloniaProperty.Register<VideoPlayerControl, bool>("AutoPlay", false, false, (BindingMode)1, (Func<bool, bool>)null, (Func<AvaloniaObject, bool, bool>)null, false);

		/// <summary>
		/// Defines the ShowControls property.
		/// </summary>
		public static readonly StyledProperty<bool> ShowControlsProperty = AvaloniaProperty.Register<VideoPlayerControl, bool>("ShowControls", true, false, (BindingMode)1, (Func<bool, bool>)null, (Func<AvaloniaObject, bool, bool>)null, false);

		/// <summary>
		/// Defines the ShowOpenButton property.
		/// </summary>
		public static readonly StyledProperty<bool> ShowOpenButtonProperty = AvaloniaProperty.Register<VideoPlayerControl, bool>("ShowOpenButton", true, false, (BindingMode)1, (Func<bool, bool>)null, (Func<AvaloniaObject, bool, bool>)null, false);

		/// <summary>
		/// Defines the Source property for setting video path directly.
		/// </summary>
		public static readonly StyledProperty<string?> SourceProperty = AvaloniaProperty.Register<VideoPlayerControl, string>("Source", (string)null, false, (BindingMode)1, (Func<string, bool>)null, (Func<AvaloniaObject, string, string>)null, false);

		/// <summary>
		/// Defines the ControlPanelBackground property.
		/// </summary>
		public static readonly StyledProperty<IBrush?> ControlPanelBackgroundProperty = AvaloniaProperty.Register<VideoPlayerControl, IBrush>("ControlPanelBackground", (IBrush)null, false, (BindingMode)1, (Func<IBrush, bool>)null, (Func<AvaloniaObject, IBrush, IBrush>)null, false);

		/// <summary>
		/// Defines the VideoBackground property.
		/// </summary>
		public static readonly StyledProperty<IBrush?> VideoBackgroundProperty = AvaloniaProperty.Register<VideoPlayerControl, IBrush>("VideoBackground", (IBrush)null, false, (BindingMode)1, (Func<IBrush, bool>)null, (Func<AvaloniaObject, IBrush, IBrush>)null, false);

		/// <summary>
		/// Defines the VideoStretch property.
		/// </summary>
		public static readonly StyledProperty<Stretch> VideoStretchProperty = AvaloniaProperty.Register<VideoPlayerControl, Stretch>("VideoStretch", (Stretch)2, false, (BindingMode)1, (Func<Stretch, bool>)null, (Func<AvaloniaObject, Stretch, Stretch>)null, false);

		/// <summary>
		/// Defines the EnableKeyboardShortcuts property.
		/// </summary>
		public static readonly StyledProperty<bool> EnableKeyboardShortcutsProperty = AvaloniaProperty.Register<VideoPlayerControl, bool>("EnableKeyboardShortcuts", true, false, (BindingMode)1, (Func<bool, bool>)null, (Func<AvaloniaObject, bool, bool>)null, false);

		/// <summary>
		/// Defines the AudioPlayerFactory property for injecting custom audio player implementations.
		/// If null, audio playback will be disabled (video-only mode).
		/// </summary>
		public static readonly StyledProperty<Func<int, int, IAudioPlayer?>?> AudioPlayerFactoryProperty = AvaloniaProperty.Register<VideoPlayerControl, Func<int, int, IAudioPlayer>>("AudioPlayerFactory", (Func<int, int, IAudioPlayer>)null, false, (BindingMode)1, (Func<Func<int, int, IAudioPlayer>, bool>)null, (Func<AvaloniaObject, Func<int, int, IAudioPlayer>, Func<int, int, IAudioPlayer>>)null, false);

		/// <summary>
		/// Defines the IconProvider property for injecting custom icon geometries.
		/// If null, default Avalonia shapes will be used.
		/// </summary>
		public static readonly StyledProperty<IIconProvider?> IconProviderProperty = AvaloniaProperty.Register<VideoPlayerControl, IIconProvider>("IconProvider", (IIconProvider)null, false, (BindingMode)1, (Func<IIconProvider, bool>)null, (Func<AvaloniaObject, IIconProvider, IIconProvider>)null, false);

		/// <summary>
		/// Defines the RenderingMode property.
		/// </summary>
		public static readonly StyledProperty<VideoRenderingMode> RenderingModeProperty = AvaloniaProperty.Register<VideoPlayerControl, VideoRenderingMode>("RenderingMode", VideoRenderingMode.Cpu, false, (BindingMode)1, (Func<VideoRenderingMode, bool>)null, (Func<AvaloniaObject, VideoRenderingMode, VideoRenderingMode>)null, false);

		/// <summary>Defines the IconSize property.</summary>
		public static readonly StyledProperty<double> IconSizeProperty = AvaloniaProperty.Register<VideoPlayerControl, double>("IconSize", 14.0, false, (BindingMode)1, (Func<double, bool>)null, (Func<AvaloniaObject, double, double>)null, false);

		/// <summary>Defines the ControlFontSize property.</summary>
		public static readonly StyledProperty<double> ControlFontSizeProperty = AvaloniaProperty.Register<VideoPlayerControl, double>("ControlFontSize", 11.0, false, (BindingMode)1, (Func<double, bool>)null, (Func<AvaloniaObject, double, double>)null, false);

		/// <summary>Defines the ButtonPadding property.</summary>
		public static readonly StyledProperty<Thickness> ButtonPaddingProperty = AvaloniaProperty.Register<VideoPlayerControl, Thickness>("ButtonPadding", new Thickness(6.0, 3.0), false, (BindingMode)1, (Func<Thickness, bool>)null, (Func<AvaloniaObject, Thickness, Thickness>)null, false);

		/// <summary>Defines the ControlPanelPadding property.</summary>
		public static readonly StyledProperty<Thickness> ControlPanelPaddingProperty = AvaloniaProperty.Register<VideoPlayerControl, Thickness>("ControlPanelPadding", new Thickness(6.0, 4.0), false, (BindingMode)1, (Func<Thickness, bool>)null, (Func<AvaloniaObject, Thickness, Thickness>)null, false);

		/// <summary>Defines the ButtonCornerRadius property.</summary>
		public static readonly StyledProperty<CornerRadius> ButtonCornerRadiusProperty = AvaloniaProperty.Register<VideoPlayerControl, CornerRadius>("ButtonCornerRadius", new CornerRadius(3.0), false, (BindingMode)1, (Func<CornerRadius, bool>)null, (Func<AvaloniaObject, CornerRadius, CornerRadius>)null, false);

		/// <summary>Defines the ControlForeground property.</summary>
		public static readonly StyledProperty<IBrush?> ControlForegroundProperty = AvaloniaProperty.Register<VideoPlayerControl, IBrush>("ControlForeground", (IBrush)null, false, (BindingMode)1, (Func<IBrush, bool>)null, (Func<AvaloniaObject, IBrush, IBrush>)null, false);

		/// <summary>Defines the ButtonBackground property.</summary>
		public static readonly StyledProperty<IBrush?> ButtonBackgroundProperty = AvaloniaProperty.Register<VideoPlayerControl, IBrush>("ButtonBackground", (IBrush)null, false, (BindingMode)1, (Func<IBrush, bool>)null, (Func<AvaloniaObject, IBrush, IBrush>)null, false);

		/// <summary>Defines the ShowFullscreenButton property.</summary>
		public static readonly StyledProperty<bool> ShowFullscreenButtonProperty = AvaloniaProperty.Register<VideoPlayerControl, bool>("ShowFullscreenButton", false, false, (BindingMode)1, (Func<bool, bool>)null, (Func<AvaloniaObject, bool, bool>)null, false);

		/// <summary>Defines the IsFullscreen property.</summary>
		public static readonly StyledProperty<bool> IsFullscreenProperty = AvaloniaProperty.Register<VideoPlayerControl, bool>("IsFullscreen", false, false, (BindingMode)1, (Func<bool, bool>)null, (Func<AvaloniaObject, bool, bool>)null, false);

		[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "11.3.6.0")]
		internal UserControl Root;

		[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "11.3.6.0")]
		internal Border VideoBorder;

		[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "11.3.6.0")]
		internal Image VideoImage;

		[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "11.3.6.0")]
		internal Border ControlPanelBorder;

		[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "11.3.6.0")]
		internal Button OpenButton;

		[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "11.3.6.0")]
		internal Path FolderIcon;

		[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "11.3.6.0")]
		internal Button PlayPauseButton;

		[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "11.3.6.0")]
		internal Path PlayPauseIcon;

		[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "11.3.6.0")]
		internal Button StopButton;

		[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "11.3.6.0")]
		internal Path StopIcon;

		[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "11.3.6.0")]
		internal TextBlock CurrentTimeText;

		[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "11.3.6.0")]
		internal Slider SeekBar;

		[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "11.3.6.0")]
		internal TextBlock TotalTimeText;

		[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "11.3.6.0")]
		internal Button VolumeButton;

		[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "11.3.6.0")]
		internal Path VolumeIcon;

		[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "11.3.6.0")]
		internal Slider VolumeSlider;

		[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "11.3.6.0")]
		internal Button FullscreenButton;

		[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "11.3.6.0")]
		internal Path FullscreenIcon;

		[CompilerGenerated]
		private static Action<object?>? !XamlIlPopulateOverride;

		/// <summary>
		/// Gets or sets the volume (0-100).
		/// </summary>
		public int Volume
		{
			get
			{
				return ((AvaloniaObject)this).GetValue<int>(VolumeProperty);
			}
			set
			{
				((AvaloniaObject)this).SetValue<int>(VolumeProperty, Math.Clamp(value, 0, 100), (BindingPriority)0);
				if (_mediaPlayer != null)
				{
					_mediaPlayer.Volume = value;
				}
				if (_volumeSlider != null)
				{
					((RangeBase)_volumeSlider).Value = value;
				}
			}
		}

		/// <summary>
		/// Gets or sets whether the video should auto-play when opened.
		/// </summary>
		public bool AutoPlay
		{
			get
			{
				return ((AvaloniaObject)this).GetValue<bool>(AutoPlayProperty);
			}
			set
			{
				((AvaloniaObject)this).SetValue<bool>(AutoPlayProperty, value, (BindingPriority)0);
			}
		}

		/// <summary>
		/// Gets or sets whether the playback controls are visible.
		/// </summary>
		public bool ShowControls
		{
			get
			{
				return ((AvaloniaObject)this).GetValue<bool>(ShowControlsProperty);
			}
			set
			{
				((AvaloniaObject)this).SetValue<bool>(ShowControlsProperty, value, (BindingPriority)0);
				if (_controlPanelBorder != null)
				{
					((Visual)_controlPanelBorder).IsVisible = value;
				}
			}
		}

		/// <summary>
		/// Gets or sets whether the Open button is visible.
		/// When false, the Open button is hidden (useful for embedded players with programmatic source).
		/// </summary>
		public bool ShowOpenButton
		{
			get
			{
				return ((AvaloniaObject)this).GetValue<bool>(ShowOpenButtonProperty);
			}
			set
			{
				((AvaloniaObject)this).SetValue<bool>(ShowOpenButtonProperty, value, (BindingPriority)0);
			}
		}

		/// <summary>
		/// Gets or sets the video source path. Setting this will automatically load and play the video.
		/// </summary>
		public string? Source
		{
			get
			{
				return ((AvaloniaObject)this).GetValue<string>(SourceProperty);
			}
			set
			{
				((AvaloniaObject)this).SetValue<string>(SourceProperty, value, (BindingPriority)0);
			}
		}

		/// <summary>
		/// Gets or sets the background brush for the control panel.
		/// Default is White. Set to any brush to customize the appearance.
		/// </summary>
		public IBrush? ControlPanelBackground
		{
			get
			{
				return ((AvaloniaObject)this).GetValue<IBrush>(ControlPanelBackgroundProperty);
			}
			set
			{
				((AvaloniaObject)this).SetValue<IBrush>(ControlPanelBackgroundProperty, value, (BindingPriority)0);
			}
		}

		/// <summary>
		/// Gets or sets the background brush for the video display area.
		/// Default is null (transparent). Set to a brush (e.g., Brushes.Black) to show a background color.
		/// When null or Transparent, the background will be transparent, allowing the parent control's background to show through.
		/// This is especially useful when playing videos with transparency or when no video is loaded.
		/// </summary>
		public IBrush? VideoBackground
		{
			get
			{
				return ((AvaloniaObject)this).GetValue<IBrush>(VideoBackgroundProperty);
			}
			set
			{
				((AvaloniaObject)this).SetValue<IBrush>(VideoBackgroundProperty, value, (BindingPriority)0);
			}
		}

		/// <summary>
		/// Gets or sets the stretch mode for the video.
		/// Default is Uniform. Options: None, Fill, Uniform, UniformToFill.
		/// </summary>
		public Stretch VideoStretch
		{
			get
			{
				//IL_0006: Unknown result type (might be due to invalid IL or missing references)
				return ((AvaloniaObject)this).GetValue<Stretch>(VideoStretchProperty);
			}
			set
			{
				//IL_0006: Unknown result type (might be due to invalid IL or missing references)
				((AvaloniaObject)this).SetValue<Stretch>(VideoStretchProperty, value, (BindingPriority)0);
			}
		}

		/// <summary>
		/// Gets or sets whether keyboard shortcuts are enabled.
		/// Default is true.
		/// </summary>
		public bool EnableKeyboardShortcuts
		{
			get
			{
				return ((AvaloniaObject)this).GetValue<bool>(EnableKeyboardShortcutsProperty);
			}
			set
			{
				((AvaloniaObject)this).SetValue<bool>(EnableKeyboardShortcutsProperty, value, (BindingPriority)0);
			}
		}

		/// <summary>
		/// Gets or sets the factory function for creating audio players.
		/// Signature: (sampleRate, channels) =&gt; IAudioPlayer?
		/// If null, audio playback will be disabled (video-only mode).
		/// </summary>
		public Func<int, int, IAudioPlayer?>? AudioPlayerFactory
		{
			get
			{
				return ((AvaloniaObject)this).GetValue<Func<int, int, IAudioPlayer>>(AudioPlayerFactoryProperty);
			}
			set
			{
				((AvaloniaObject)this).SetValue<Func<int, int, IAudioPlayer>>(AudioPlayerFactoryProperty, value, (BindingPriority)0);
			}
		}

		/// <summary>
		/// Gets or sets the icon provider for custom icon geometries.
		/// If null, default Avalonia shapes will be used.
		/// </summary>
		public IIconProvider? IconProvider
		{
			get
			{
				return ((AvaloniaObject)this).GetValue<IIconProvider>(IconProviderProperty);
			}
			set
			{
				((AvaloniaObject)this).SetValue<IIconProvider>(IconProviderProperty, value, (BindingPriority)0);
			}
		}

		/// <summary>
		/// Gets or sets the video rendering mode.
		/// Cpu: Uses WriteableBitmap (default, backward compatible).
		/// OpenGL: Uses hardware-accelerated OpenGL rendering (requires OpenGL support).
		/// </summary>
		public VideoRenderingMode RenderingMode
		{
			get
			{
				return ((AvaloniaObject)this).GetValue<VideoRenderingMode>(RenderingModeProperty);
			}
			set
			{
				((AvaloniaObject)this).SetValue<VideoRenderingMode>(RenderingModeProperty, value, (BindingPriority)0);
			}
		}

		/// <summary>
		/// Gets or sets the size of playback control icons. Default is 14.
		/// </summary>
		public double IconSize
		{
			get
			{
				return ((AvaloniaObject)this).GetValue<double>(IconSizeProperty);
			}
			set
			{
				((AvaloniaObject)this).SetValue<double>(IconSizeProperty, value, (BindingPriority)0);
			}
		}

		/// <summary>
		/// Gets or sets the font size for control panel text. Default is 11.
		/// </summary>
		public double ControlFontSize
		{
			get
			{
				return ((AvaloniaObject)this).GetValue<double>(ControlFontSizeProperty);
			}
			set
			{
				((AvaloniaObject)this).SetValue<double>(ControlFontSizeProperty, value, (BindingPriority)0);
			}
		}

		/// <summary>
		/// Gets or sets the padding inside playback buttons. Default is 6,3.
		/// </summary>
		public Thickness ButtonPadding
		{
			get
			{
				//IL_0006: Unknown result type (might be due to invalid IL or missing references)
				return ((AvaloniaObject)this).GetValue<Thickness>(ButtonPaddingProperty);
			}
			set
			{
				//IL_0006: Unknown result type (might be due to invalid IL or missing references)
				((AvaloniaObject)this).SetValue<Thickness>(ButtonPaddingProperty, value, (BindingPriority)0);
			}
		}

		/// <summary>
		/// Gets or sets the padding of the entire control panel. Default is 6,4.
		/// </summary>
		public Thickness ControlPanelPadding
		{
			get
			{
				//IL_0006: Unknown result type (might be due to invalid IL or missing references)
				return ((AvaloniaObject)this).GetValue<Thickness>(ControlPanelPaddingProperty);
			}
			set
			{
				//IL_0006: Unknown result type (might be due to invalid IL or missing references)
				((AvaloniaObject)this).SetValue<Thickness>(ControlPanelPaddingProperty, value, (BindingPriority)0);
			}
		}

		/// <summary>
		/// Gets or sets the corner radius for playback buttons. Default is 3.
		/// </summary>
		public CornerRadius ButtonCornerRadius
		{
			get
			{
				//IL_0006: Unknown result type (might be due to invalid IL or missing references)
				return ((AvaloniaObject)this).GetValue<CornerRadius>(ButtonCornerRadiusProperty);
			}
			set
			{
				//IL_0006: Unknown result type (might be due to invalid IL or missing references)
				((AvaloniaObject)this).SetValue<CornerRadius>(ButtonCornerRadiusProperty, value, (BindingPriority)0);
			}
		}

		/// <summary>
		/// Gets or sets the foreground brush for control text and icons.
		/// If null, defaults to #333333.
		/// </summary>
		public IBrush? ControlForeground
		{
			get
			{
				return ((AvaloniaObject)this).GetValue<IBrush>(ControlForegroundProperty);
			}
			set
			{
				((AvaloniaObject)this).SetValue<IBrush>(ControlForegroundProperty, value, (BindingPriority)0);
			}
		}

		/// <summary>
		/// Gets or sets the background brush for playback buttons.
		/// If null, defaults to #e8e8e8.
		/// </summary>
		public IBrush? ButtonBackground
		{
			get
			{
				return ((AvaloniaObject)this).GetValue<IBrush>(ButtonBackgroundProperty);
			}
			set
			{
				((AvaloniaObject)this).SetValue<IBrush>(ButtonBackgroundProperty, value, (BindingPriority)0);
			}
		}

		/// <summary>
		/// Gets the full path of the currently loaded media file, if any.
		/// </summary>
		public string? CurrentMediaPath => _currentMediaPath;

		/// <summary>
		/// Gets whether the control currently has a media resource loaded.
		/// </summary>
		public bool HasMediaLoaded => _hasMediaLoaded;

		/// <summary>
		/// Gets whether a video is currently playing.
		/// </summary>
		public bool IsPlaying
		{
			get
			{
				FFmpegMediaPlayer? mediaPlayer = _mediaPlayer;
				if (mediaPlayer == null)
				{
					return false;
				}
				return mediaPlayer.IsPlaying;
			}
		}

		/// <summary>
		/// Gets the current playback position in milliseconds.
		/// </summary>
		public long Position
		{
			get
			{
				if (_mediaPlayer == null)
				{
					return 0L;
				}
				return (long)(_mediaPlayer.Position * (float)_mediaPlayer.Length);
			}
		}

		/// <summary>
		/// Gets the total duration of the current media in milliseconds.
		/// </summary>
		public long Duration
		{
			get
			{
				FFmpegMediaPlayer? mediaPlayer = _mediaPlayer;
				if (mediaPlayer == null)
				{
					return 0L;
				}
				return mediaPlayer.Length;
			}
		}

		/// <summary>
		/// Gets or sets whether the fullscreen toggle button is visible. Default is false.
		/// </summary>
		public bool ShowFullscreenButton
		{
			get
			{
				return ((AvaloniaObject)this).GetValue<bool>(ShowFullscreenButtonProperty);
			}
			set
			{
				((AvaloniaObject)this).SetValue<bool>(ShowFullscreenButtonProperty, value, (BindingPriority)0);
			}
		}

		/// <summary>
		/// Gets or sets whether the player is in fullscreen mode. Controls the fullscreen button icon.
		/// </summary>
		public bool IsFullscreen
		{
			get
			{
				return ((AvaloniaObject)this).GetValue<bool>(IsFullscreenProperty);
			}
			set
			{
				((AvaloniaObject)this).SetValue<bool>(IsFullscreenProperty, value, (BindingPriority)0);
			}
		}

		/// <summary>
		/// Occurs when the fullscreen button is clicked. The consuming app handles the actual resize.
		/// </summary>
		public event EventHandler? FullscreenToggle;

		/// <summary>
		/// Occurs when playback starts.
		/// </summary>
		public event EventHandler? PlaybackStarted;

		/// <summary>
		/// Occurs when media is successfully opened.
		/// </summary>
		public event EventHandler<MediaOpenedEventArgs>? MediaOpened;

		/// <summary>
		/// Occurs when playback is paused.
		/// </summary>
		public event EventHandler? PlaybackPaused;

		/// <summary>
		/// Occurs when playback is stopped.
		/// </summary>
		public event EventHandler? PlaybackStopped;

		/// <summary>
		/// Occurs when the media ends.
		/// </summary>
		public event EventHandler? MediaEnded;

		/// <summary>
		/// Creates a new instance of the VideoPlayerControl.
		/// </summary>
		public VideoPlayerControl()
		{
			InitializeComponent();
			((InputElement)this).Focusable = true;
			_seekBar = ControlExtensions.FindControl<Slider>((Control)(object)this, "SeekBar");
			_volumeSlider = ControlExtensions.FindControl<Slider>((Control)(object)this, "VolumeSlider");
			_currentTimeText = ControlExtensions.FindControl<TextBlock>((Control)(object)this, "CurrentTimeText");
			_totalTimeText = ControlExtensions.FindControl<TextBlock>((Control)(object)this, "TotalTimeText");
			_playPauseIcon = ControlExtensions.FindControl<Path>((Control)(object)this, "PlayPauseIcon");
			_playPauseText = ControlExtensions.FindControl<TextBlock>((Control)(object)this, "PlayPauseText");
			_volumeIcon = ControlExtensions.FindControl<Path>((Control)(object)this, "VolumeIcon");
			_controlPanelBorder = ControlExtensions.FindControl<Border>((Control)(object)this, "ControlPanelBorder");
			_videoBorder = ControlExtensions.FindControl<Border>((Control)(object)this, "VideoBorder");
			_openButton = ControlExtensions.FindControl<Button>((Control)(object)this, "OpenButton");
			if (_controlPanelBorder != null)
			{
				((Visual)_controlPanelBorder).IsVisible = ShowControls;
			}
			if (_openButton != null)
			{
				((Visual)_openButton).IsVisible = ShowOpenButton;
			}
			if (_seekBar != null)
			{
				((Interactive)_seekBar).AddHandler<PointerPressedEventArgs>(InputElement.PointerPressedEvent, (EventHandler<PointerPressedEventArgs>)OnSeekBarPointerPressed, (RoutingStrategies)2, false);
				((Interactive)_seekBar).AddHandler<PointerReleasedEventArgs>(InputElement.PointerReleasedEvent, (EventHandler<PointerReleasedEventArgs>)OnSeekBarPointerReleased, (RoutingStrategies)2, false);
				((Interactive)_seekBar).AddHandler<PointerCaptureLostEventArgs>(InputElement.PointerCaptureLostEvent, (EventHandler<PointerCaptureLostEventArgs>)OnSeekBarPointerCaptureLost, (RoutingStrategies)2, false);
			}
			if (_volumeSlider != null)
			{
				((RangeBase)_volumeSlider).ValueChanged += OnVolumeChanged;
			}
			((Visual)this).AttachedToVisualTree += OnAttachedToVisualTree;
			((Visual)this).DetachedFromVisualTree += OnDetachedFromVisualTree;
			((AvaloniaObject)this).PropertyChanged += OnPropertyChanged;
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			//IL_0011: Unknown result type (might be due to invalid IL or missing references)
			//IL_0016: Unknown result type (might be due to invalid IL or missing references)
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_002a: Unknown result type (might be due to invalid IL or missing references)
			//IL_002f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0030: Unknown result type (might be due to invalid IL or missing references)
			//IL_0033: Unknown result type (might be due to invalid IL or missing references)
			//IL_005d: Expected I4, but got Unknown
			//IL_005d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0060: Invalid comparison between Unknown and I4
			((InputElement)this).OnKeyDown(e);
			if (!EnableKeyboardShortcuts)
			{
				return;
			}
			KeyModifiers keyModifiers = e.KeyModifiers;
			bool flag = ((Enum)keyModifiers).HasFlag((Enum)(object)(KeyModifiers)2);
			Key key = e.Key;
			switch (key - 18)
			{
			default:
				if ((int)key == 56)
				{
					ToggleMute();
					((RoutedEventArgs)e).Handled = true;
				}
				break;
			case 0:
				TogglePlayPause();
				((RoutedEventArgs)e).Handled = true;
				break;
			case 5:
				if (_mediaPlayer != null && _mediaPlayer.Length > 0)
				{
					double num3 = _mediaPlayer.Position * (float)_mediaPlayer.Length;
					double num4 = (flag ? 30000 : 5000);
					float positionPercent2 = (float)(Math.Max(0.0, num3 - num4) / (double)_mediaPlayer.Length);
					Seek(positionPercent2);
				}
				((RoutedEventArgs)e).Handled = true;
				break;
			case 7:
				if (_mediaPlayer != null && _mediaPlayer.Length > 0)
				{
					double num = _mediaPlayer.Position * (float)_mediaPlayer.Length;
					double num2 = (flag ? 30000 : 5000);
					float positionPercent = (float)(Math.Min(_mediaPlayer.Length, num + num2) / (double)_mediaPlayer.Length);
					Seek(positionPercent);
				}
				((RoutedEventArgs)e).Handled = true;
				break;
			case 6:
				Volume = Math.Min(100, Volume + 5);
				((RoutedEventArgs)e).Handled = true;
				break;
			case 8:
				Volume = Math.Max(0, Volume - 5);
				((RoutedEventArgs)e).Handled = true;
				break;
			case 1:
			case 2:
			case 3:
			case 4:
				break;
			}
		}

		private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
		{
			//IL_0120: Unknown result type (might be due to invalid IL or missing references)
			//IL_0125: Unknown result type (might be due to invalid IL or missing references)
			//IL_0139: Unknown result type (might be due to invalid IL or missing references)
			//IL_0156: Unknown result type (might be due to invalid IL or missing references)
			if (e.Property == (AvaloniaProperty)(object)SourceProperty)
			{
				string text = e.NewValue as string;
				if (!string.IsNullOrEmpty(text) && _isInitialized)
				{
					Open(text);
					if (AutoPlay)
					{
						Play();
					}
				}
			}
			else if (e.Property == (AvaloniaProperty)(object)ShowOpenButtonProperty)
			{
				if (_openButton != null)
				{
					((Visual)_openButton).IsVisible = (bool)(e.NewValue ?? ((object)true));
				}
			}
			else if (e.Property == (AvaloniaProperty)(object)ControlPanelBackgroundProperty)
			{
				if (_controlPanelBorder != null)
				{
					object newValue = e.NewValue;
					IBrush val = (IBrush)((newValue is IBrush) ? newValue : null);
					if (val != null)
					{
						_controlPanelBorder.Background = val;
					}
				}
			}
			else if (e.Property == (AvaloniaProperty)(object)VideoBackgroundProperty)
			{
				if (_videoBorder != null)
				{
					Border? videoBorder = _videoBorder;
					object newValue2 = e.NewValue;
					videoBorder.Background = (IBrush)((newValue2 is IBrush) ? newValue2 : null);
				}
			}
			else if (e.Property == (AvaloniaProperty)(object)VideoStretchProperty)
			{
				if (e.NewValue is Stretch stretch)
				{
					if (_videoRenderer is CpuVideoRenderer cpuVideoRenderer)
					{
						cpuVideoRenderer.Stretch = stretch;
					}
					else if (_videoRenderer is OpenGLVideoRenderer openGLVideoRenderer)
					{
						openGLVideoRenderer.Stretch = stretch;
					}
				}
			}
			else if (e.Property == (AvaloniaProperty)(object)ShowControlsProperty)
			{
				if (_controlPanelBorder != null)
				{
					((Visual)_controlPanelBorder).IsVisible = (bool)(e.NewValue ?? ((object)true));
				}
			}
			else if (e.Property == (AvaloniaProperty)(object)RenderingModeProperty)
			{
				SetupVideoRenderer();
			}
			else if (e.Property == (AvaloniaProperty)(object)ShowFullscreenButtonProperty)
			{
				Button val2 = ControlExtensions.FindControl<Button>((Control)(object)this, "FullscreenButton");
				if (val2 != null)
				{
					((Visual)val2).IsVisible = (bool)(e.NewValue ?? ((object)false));
				}
			}
			else if (e.Property == (AvaloniaProperty)(object)IsFullscreenProperty)
			{
				UpdateFullscreenIcon();
			}
			else if (e.Property == (AvaloniaProperty)(object)ControlForegroundProperty)
			{
				object newValue3 = e.NewValue;
				ApplyControlForeground((IBrush?)((newValue3 is IBrush) ? newValue3 : null));
			}
			else if (e.Property == (AvaloniaProperty)(object)ButtonBackgroundProperty)
			{
				object newValue4 = e.NewValue;
				ApplyButtonBackground((IBrush?)((newValue4 is IBrush) ? newValue4 : null));
			}
		}

		private void ApplyControlForeground(IBrush? brush)
		{
			if (brush == null)
			{
				return;
			}
			Path[] array = (Path[])(object)new Path[4]
			{
				ControlExtensions.FindControl<Path>((Control)(object)this, "FolderIcon"),
				_playPauseIcon,
				ControlExtensions.FindControl<Path>((Control)(object)this, "StopIcon"),
				_volumeIcon
			};
			foreach (Path val in array)
			{
				if (val != null)
				{
					((Shape)val).Fill = brush;
				}
			}
			if (_currentTimeText != null)
			{
				_currentTimeText.Foreground = brush;
			}
			if (_totalTimeText != null)
			{
				_totalTimeText.Foreground = brush;
			}
		}

		private void ApplyButtonBackground(IBrush? brush)
		{
			if (brush == null)
			{
				return;
			}
			Button[] array = (Button[])(object)new Button[3]
			{
				_openButton,
				ControlExtensions.FindControl<Button>((Control)(object)this, "PlayPauseButton"),
				ControlExtensions.FindControl<Button>((Control)(object)this, "StopButton")
			};
			foreach (Button val in array)
			{
				if (val != null)
				{
					((TemplatedControl)val).Background = brush;
				}
			}
		}

		private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
		{
			InitializePlayer();
		}

		private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
		{
			Cleanup();
		}

		private void InitializePlayer()
		{
			//IL_0065: Unknown result type (might be due to invalid IL or missing references)
			//IL_006f: Expected O, but got Unknown
			//IL_01ad: Unknown result type (might be due to invalid IL or missing references)
			//IL_01ca: Unknown result type (might be due to invalid IL or missing references)
			if (_isInitialized)
			{
				return;
			}
			try
			{
				if (!FFmpegInitializer.IsInitialized)
				{
					FFmpegInitializer.Initialize((string)null, true, true);
				}
				Func<int, int, IAudioPlayer> func = AudioPlayerFactory ?? ((Func<int, int, IAudioPlayer>)((int sr, int ch) => AudioPlayerFactory.Create(sr, ch)));
				_mediaPlayer = new FFmpegMediaPlayer((Action<Action>)delegate(Action action)
				{
					//IL_0008: Unknown result type (might be due to invalid IL or missing references)
					//IL_000e: Unknown result type (might be due to invalid IL or missing references)
					Dispatcher.UIThread.Post(action, default(DispatcherPriority));
				}, func);
				_mediaPlayer.PositionChanged += OnPositionChanged;
				_mediaPlayer.LengthChanged += OnLengthChanged;
				_mediaPlayer.Playing += OnPlaying;
				_mediaPlayer.Paused += OnPaused;
				_mediaPlayer.Stopped += OnStopped;
				_mediaPlayer.EndReached += OnEndReached;
				_mediaPlayer.FrameReady += OnFrameReady;
				_isInitialized = true;
				SetupVideoRenderer();
				if (_openButton != null)
				{
					((Visual)_openButton).IsVisible = ShowOpenButton;
				}
				Button val = ControlExtensions.FindControl<Button>((Control)(object)this, "FullscreenButton");
				if (val != null)
				{
					((Visual)val).IsVisible = ShowFullscreenButton;
				}
				if (_controlPanelBorder != null)
				{
					((Visual)_controlPanelBorder).IsVisible = ShowControls;
					if (ControlPanelBackground != null)
					{
						_controlPanelBorder.Background = ControlPanelBackground;
					}
				}
				if (_videoBorder != null)
				{
					_videoBorder.Background = VideoBackground;
				}
				if (_videoRenderer is CpuVideoRenderer cpuVideoRenderer)
				{
					cpuVideoRenderer.Stretch = VideoStretch;
				}
				else if (_videoRenderer is OpenGLVideoRenderer openGLVideoRenderer)
				{
					openGLVideoRenderer.Stretch = VideoStretch;
				}
				if (!string.IsNullOrEmpty(Source))
				{
					Open(Source);
				}
			}
			catch (Exception)
			{
			}
		}

		private void SetupVideoRenderer()
		{
			//IL_0086: Unknown result type (might be due to invalid IL or missing references)
			//IL_008c: Expected O, but got Unknown
			//IL_00a2: Unknown result type (might be due to invalid IL or missing references)
			//IL_00a8: Expected O, but got Unknown
			//IL_00d8: Unknown result type (might be due to invalid IL or missing references)
			//IL_0052: Unknown result type (might be due to invalid IL or missing references)
			//IL_0058: Expected O, but got Unknown
			//IL_00f4: Unknown result type (might be due to invalid IL or missing references)
			//IL_006c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0072: Expected O, but got Unknown
			if (_videoBorder == null)
			{
				return;
			}
			if (_videoRenderer != null)
			{
				((IDisposable)_videoRenderer).Dispose();
				_videoRenderer = null;
			}
			((Decorator)_videoBorder).Child = null;
			Control val = null;
			try
			{
				switch (RenderingMode)
				{
				case VideoRenderingMode.Cpu:
					_videoRenderer = (IVideoRenderer?)(object)new CpuVideoRenderer();
					val = (Control)_videoRenderer;
					break;
				case VideoRenderingMode.OpenGL:
					try
					{
						_videoRenderer = (IVideoRenderer?)(object)new OpenGLVideoRenderer();
						val = (Control)_videoRenderer;
					}
					catch (Exception)
					{
						_videoRenderer = (IVideoRenderer?)(object)new CpuVideoRenderer();
						val = (Control)_videoRenderer;
					}
					break;
				}
			}
			catch (Exception)
			{
				_videoRenderer = (IVideoRenderer?)(object)new CpuVideoRenderer();
				val = (Control)_videoRenderer;
			}
			if (val != null)
			{
				((Layoutable)val).HorizontalAlignment = (HorizontalAlignment)0;
				((Layoutable)val).VerticalAlignment = (VerticalAlignment)0;
				((Decorator)_videoBorder).Child = val;
				if (_videoRenderer is CpuVideoRenderer cpuVideoRenderer)
				{
					cpuVideoRenderer.Stretch = VideoStretch;
				}
				else if (_videoRenderer is OpenGLVideoRenderer openGLVideoRenderer)
				{
					openGLVideoRenderer.Stretch = VideoStretch;
				}
			}
		}

		private void OnFrameReady(object? sender, FrameEventArgs e)
		{
			try
			{
				if (_videoRenderer != null)
				{
					_videoRenderer.RenderFrame(e.Data, e.Width, e.Height, e.Stride);
				}
			}
			catch (Exception)
			{
			}
			finally
			{
				e.Dispose();
			}
		}

		/// <summary>
		/// Opens and optionally plays a media file.
		/// </summary>
		/// <param name="path">The path to the media file.</param>
		public void Open(string path)
		{
			if (_mediaPlayer == null)
			{
				return;
			}
			_hasMediaLoaded = false;
			if (_mediaPlayer.Open(path))
			{
				_currentMediaPath = path;
				_hasMediaLoaded = true;
				this.MediaOpened?.Invoke(this, new MediaOpenedEventArgs(path));
				_mediaPlayer.DecodeFirstFrame();
				if (AutoPlay)
				{
					_mediaPlayer.Play();
				}
			}
		}

		/// <summary>
		/// Opens and optionally plays a media from a URI.
		/// </summary>
		/// <param name="uri">The URI of the media.</param>
		public void OpenUri(Uri uri)
		{
			if (uri.IsFile)
			{
				Open(uri.LocalPath);
			}
			else
			{
				Open(uri.ToString());
			}
		}

		/// <summary>
		/// Starts or resumes playback.
		/// </summary>
		public void Play()
		{
			FFmpegMediaPlayer? mediaPlayer = _mediaPlayer;
			if (mediaPlayer != null)
			{
				mediaPlayer.Play();
			}
		}

		/// <summary>
		/// Pauses playback.
		/// </summary>
		public void Pause()
		{
			FFmpegMediaPlayer? mediaPlayer = _mediaPlayer;
			if (mediaPlayer != null)
			{
				mediaPlayer.Pause();
			}
		}

		/// <summary>
		/// Stops playback.
		/// </summary>
		public void Stop()
		{
			FFmpegMediaPlayer? mediaPlayer = _mediaPlayer;
			if (mediaPlayer != null)
			{
				mediaPlayer.Stop();
			}
			if (_seekBar != null)
			{
				((RangeBase)_seekBar).Value = 0.0;
			}
			if (_currentTimeText != null)
			{
				_currentTimeText.Text = "00:00";
			}
			IVideoRenderer? videoRenderer = _videoRenderer;
			if (videoRenderer != null)
			{
				videoRenderer.Clear();
			}
		}

		/// <summary>
		/// Steps forward exactly one frame. Pauses playback after displaying the frame.
		/// </summary>
		/// <returns>True if a frame was successfully decoded and displayed, false otherwise.</returns>
		public bool StepForward()
		{
			FFmpegMediaPlayer? mediaPlayer = _mediaPlayer;
			if (mediaPlayer == null)
			{
				return false;
			}
			return mediaPlayer.StepForward();
		}

		/// <summary>
		/// Steps backward one frame. Uses cached frames when available, otherwise seeks to previous keyframe and decodes forward.
		/// </summary>
		/// <returns>True if a frame was successfully displayed, false otherwise.</returns>
		public bool StepBackward()
		{
			FFmpegMediaPlayer? mediaPlayer = _mediaPlayer;
			if (mediaPlayer == null)
			{
				return false;
			}
			return mediaPlayer.StepBackward();
		}

		/// <summary>
		/// Toggles between play and pause.
		/// </summary>
		public void TogglePlayPause()
		{
			if (_mediaPlayer != null)
			{
				if (_mediaPlayer.IsPlaying)
				{
					_mediaPlayer.Pause();
				}
				else
				{
					_mediaPlayer.Play();
				}
			}
		}

		/// <summary>
		/// Seeks to a specific position.
		/// </summary>
		/// <param name="positionPercent">Position as a percentage (0.0 to 1.0).</param>
		public void Seek(float positionPercent)
		{
			FFmpegMediaPlayer? mediaPlayer = _mediaPlayer;
			if (mediaPlayer != null)
			{
				mediaPlayer.Seek(positionPercent);
			}
		}

		/// <summary>
		/// Toggles mute state.
		/// </summary>
		public void ToggleMute()
		{
			OnMuteClick(null, null);
		}

		private void OnPlaying(object? sender, EventArgs e)
		{
			UpdatePlayPauseButton(isPlaying: true);
			this.PlaybackStarted?.Invoke(this, EventArgs.Empty);
		}

		private void OnPaused(object? sender, EventArgs e)
		{
			UpdatePlayPauseButton(isPlaying: false);
			this.PlaybackPaused?.Invoke(this, EventArgs.Empty);
		}

		private void OnStopped(object? sender, EventArgs e)
		{
			UpdatePlayPauseButton(isPlaying: false);
			this.PlaybackStopped?.Invoke(this, EventArgs.Empty);
			IVideoRenderer? videoRenderer = _videoRenderer;
			if (videoRenderer != null)
			{
				videoRenderer.Clear();
			}
		}

		private void OnEndReached(object? sender, EventArgs e)
		{
			//IL_0013: Unknown result type (might be due to invalid IL or missing references)
			//IL_0019: Unknown result type (might be due to invalid IL or missing references)
			Dispatcher.UIThread.Post((Action)delegate
			{
				UpdatePlayPauseButton(isPlaying: false);
				if (_seekBar != null)
				{
					((RangeBase)_seekBar).Value = 100.0;
				}
				if (_currentTimeText != null && _totalTimeText != null)
				{
					_currentTimeText.Text = _totalTimeText.Text;
				}
				this.MediaEnded?.Invoke(this, EventArgs.Empty);
			}, default(DispatcherPriority));
		}

		private void OnPositionChanged(object? sender, PositionChangedEventArgs e)
		{
			//IL_0038: Unknown result type (might be due to invalid IL or missing references)
			//IL_003e: Unknown result type (might be due to invalid IL or missing references)
			if (_isDraggingSeekBar || _mediaPlayer == null)
			{
				return;
			}
			Dispatcher.UIThread.Post((Action)delegate
			{
				if (_seekBar != null)
				{
					((RangeBase)_seekBar).Value = e.Position * 100f;
				}
				if (_currentTimeText != null && _mediaPlayer.Length > 0)
				{
					TimeSpan time = TimeSpan.FromMilliseconds((float)_mediaPlayer.Length * e.Position);
					_currentTimeText.Text = FormatTime(time);
				}
			}, default(DispatcherPriority));
		}

		private void OnLengthChanged(object? sender, LengthChangedEventArgs e)
		{
			//IL_0027: Unknown result type (might be due to invalid IL or missing references)
			//IL_002d: Unknown result type (might be due to invalid IL or missing references)
			Dispatcher.UIThread.Post((Action)delegate
			{
				if (_totalTimeText != null)
				{
					TimeSpan time = TimeSpan.FromMilliseconds(e.Length);
					_totalTimeText.Text = FormatTime(time);
				}
			}, default(DispatcherPriority));
		}

		private void OnSeekBarPointerPressed(object? sender, PointerPressedEventArgs e)
		{
			_isDraggingSeekBar = true;
		}

		private void OnSeekBarPointerReleased(object? sender, PointerReleasedEventArgs e)
		{
			if (_isDraggingSeekBar)
			{
				_isDraggingSeekBar = false;
				SeekToCurrentSliderPosition();
			}
		}

		private void OnSeekBarPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
		{
			if (_isDraggingSeekBar)
			{
				_isDraggingSeekBar = false;
				SeekToCurrentSliderPosition();
			}
		}

		private void SeekToCurrentSliderPosition()
		{
			if (_mediaPlayer != null && _seekBar != null)
			{
				float num = (float)(((RangeBase)_seekBar).Value / 100.0);
				_mediaPlayer.Seek(num);
				if (!_mediaPlayer.IsPlaying)
				{
					_mediaPlayer.ShowFrameAtCurrentPosition();
				}
			}
		}

		private void OnVolumeChanged(object? sender, RangeBaseValueChangedEventArgs e)
		{
			if (_mediaPlayer != null)
			{
				_mediaPlayer.Volume = (int)e.NewValue;
			}
		}

		private async void OnOpenClick(object? sender, RoutedEventArgs e)
		{
			try
			{
				TopLevel topLevel = TopLevel.GetTopLevel((Visual)(object)this);
				if (topLevel == null)
				{
					return;
				}
				IStorageProvider storageProvider = topLevel.StorageProvider;
				FilePickerOpenOptions val = new FilePickerOpenOptions();
				((PickerOptions)val).Title = "Open Video File";
				val.AllowMultiple = false;
				List<FilePickerFileType> list = new List<FilePickerFileType>();
				FilePickerFileType val2 = new FilePickerFileType("Video Files");
				val2.Patterns = new string[9] { "*.mp4", "*.mkv", "*.avi", "*.mov", "*.wmv", "*.flv", "*.webm", "*.m4v", "*.ts" };
				list.Add(val2);
				val2 = new FilePickerFileType("Audio Files");
				val2.Patterns = new string[6] { "*.mp3", "*.wav", "*.flac", "*.aac", "*.ogg", "*.m4a" };
				list.Add(val2);
				val2 = new FilePickerFileType("All Files");
				val2.Patterns = new string[1] { "*" };
				list.Add(val2);
				val.FileTypeFilter = list;
				IReadOnlyList<IStorageFile> readOnlyList = await storageProvider.OpenFilePickerAsync(val);
				if (readOnlyList.Count > 0)
				{
					string localPath = ((IStorageItem)readOnlyList[0]).Path.LocalPath;
					Open(localPath);
					if (_hasMediaLoaded && _mediaPlayer != null && !_mediaPlayer.IsPlaying)
					{
						_mediaPlayer.Play();
					}
				}
			}
			catch (Exception)
			{
			}
		}

		private void OnPlayPauseClick(object? sender, RoutedEventArgs e)
		{
			TogglePlayPause();
		}

		private void OnStopClick(object? sender, RoutedEventArgs e)
		{
			Stop();
		}

		private void OnMuteClick(object? sender, RoutedEventArgs e)
		{
			if (_mediaPlayer == null || _volumeIcon == null)
			{
				return;
			}
			_isMuted = !_isMuted;
			if (_isMuted)
			{
				_previousVolume = _mediaPlayer.Volume;
				_mediaPlayer.Volume = 0;
				if (_volumeSlider != null)
				{
					((RangeBase)_volumeSlider).Value = 0.0;
				}
				_volumeIcon.Data = GetIconProvider().CreateVolumeOffIcon();
			}
			else
			{
				_mediaPlayer.Volume = _previousVolume;
				if (_volumeSlider != null)
				{
					((RangeBase)_volumeSlider).Value = _previousVolume;
				}
				_volumeIcon.Data = GetIconProvider().CreateVolumeHighIcon();
			}
		}

		private void OnFullscreenClick(object? sender, RoutedEventArgs e)
		{
			IsFullscreen = !IsFullscreen;
			UpdateFullscreenIcon();
			this.FullscreenToggle?.Invoke(this, EventArgs.Empty);
		}

		private void UpdateFullscreenIcon()
		{
			Path val = ControlExtensions.FindControl<Path>((Control)(object)this, "FullscreenIcon");
			if (val != null)
			{
				IIconProvider iconProvider = GetIconProvider();
				val.Data = (IsFullscreen ? iconProvider.CreateFullscreenExitIcon() : iconProvider.CreateFullscreenIcon());
			}
		}

		private void UpdatePlayPauseButton(bool isPlaying)
		{
			//IL_0027: Unknown result type (might be due to invalid IL or missing references)
			//IL_002d: Unknown result type (might be due to invalid IL or missing references)
			Dispatcher.UIThread.Post((Action)delegate
			{
				IIconProvider iconProvider = GetIconProvider();
				if (_playPauseIcon != null)
				{
					_playPauseIcon.Data = (isPlaying ? iconProvider.CreatePauseIcon() : iconProvider.CreatePlayIcon());
				}
				if (_playPauseText != null)
				{
					_playPauseText.Text = (isPlaying ? "Pause" : "Play");
				}
			}, default(DispatcherPriority));
		}

		private IIconProvider GetIconProvider()
		{
			return IconProvider ?? DefaultIconProvider.Instance;
		}

		private static string FormatTime(TimeSpan time)
		{
			if (time.Hours > 0)
			{
				return $"{time.Hours:D2}:{time.Minutes:D2}:{time.Seconds:D2}";
			}
			return $"{time.Minutes:D2}:{time.Seconds:D2}";
		}

		private void Cleanup()
		{
			if (_mediaPlayer != null)
			{
				_mediaPlayer.PositionChanged -= OnPositionChanged;
				_mediaPlayer.LengthChanged -= OnLengthChanged;
				_mediaPlayer.Playing -= OnPlaying;
				_mediaPlayer.Paused -= OnPaused;
				_mediaPlayer.Stopped -= OnStopped;
				_mediaPlayer.EndReached -= OnEndReached;
				_mediaPlayer.FrameReady -= OnFrameReady;
				_mediaPlayer.Dispose();
				_mediaPlayer = null;
			}
			if (_videoRenderer != null)
			{
				((IDisposable)_videoRenderer).Dispose();
				_videoRenderer = null;
			}
			_isInitialized = false;
			_currentMediaPath = null;
			_hasMediaLoaded = false;
		}

		/// <summary>
		/// Wires up the controls and optionally loads XAML markup and attaches dev tools (if Avalonia.Diagnostics package is referenced).
		/// </summary>
		/// <param name="loadXaml">Should the XAML be loaded into the component.</param>
		[GeneratedCode("Avalonia.Generators.NameGenerator.InitializeComponentCodeGenerator", "11.3.6.0")]
		[ExcludeFromCodeCoverage]
		public void InitializeComponent(bool loadXaml = true)
		{
			if (loadXaml)
			{
				!XamlIlPopulateTrampoline(this);
			}
			INameScope val = NameScopeExtensions.FindNameScope((ILogical)(object)this);
			Root = ((val != null) ? NameScopeExtensions.Find<UserControl>(val, "Root") : null);
			VideoBorder = ((val != null) ? NameScopeExtensions.Find<Border>(val, "VideoBorder") : null);
			VideoImage = ((val != null) ? NameScopeExtensions.Find<Image>(val, "VideoImage") : null);
			ControlPanelBorder = ((val != null) ? NameScopeExtensions.Find<Border>(val, "ControlPanelBorder") : null);
			OpenButton = ((val != null) ? NameScopeExtensions.Find<Button>(val, "OpenButton") : null);
			FolderIcon = ((val != null) ? NameScopeExtensions.Find<Path>(val, "FolderIcon") : null);
			PlayPauseButton = ((val != null) ? NameScopeExtensions.Find<Button>(val, "PlayPauseButton") : null);
			PlayPauseIcon = ((val != null) ? NameScopeExtensions.Find<Path>(val, "PlayPauseIcon") : null);
			StopButton = ((val != null) ? NameScopeExtensions.Find<Button>(val, "StopButton") : null);
			StopIcon = ((val != null) ? NameScopeExtensions.Find<Path>(val, "StopIcon") : null);
			CurrentTimeText = ((val != null) ? NameScopeExtensions.Find<TextBlock>(val, "CurrentTimeText") : null);
			SeekBar = ((val != null) ? NameScopeExtensions.Find<Slider>(val, "SeekBar") : null);
			TotalTimeText = ((val != null) ? NameScopeExtensions.Find<TextBlock>(val, "TotalTimeText") : null);
			VolumeButton = ((val != null) ? NameScopeExtensions.Find<Button>(val, "VolumeButton") : null);
			VolumeIcon = ((val != null) ? NameScopeExtensions.Find<Path>(val, "VolumeIcon") : null);
			VolumeSlider = ((val != null) ? NameScopeExtensions.Find<Slider>(val, "VolumeSlider") : null);
			FullscreenButton = ((val != null) ? NameScopeExtensions.Find<Button>(val, "FullscreenButton") : null);
			FullscreenIcon = ((val != null) ? NameScopeExtensions.Find<Path>(val, "FullscreenIcon") : null);
		}

		[CompilerGenerated]
		private unsafe static void !XamlIlPopulate(IServiceProvider? P_0, VideoPlayerControl? P_1)
		{
			//IL_016b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0170: Unknown result type (might be due to invalid IL or missing references)
			//IL_0173: Expected O, but got Unknown
			//IL_019e: Unknown result type (might be due to invalid IL or missing references)
			//IL_01a3: Unknown result type (might be due to invalid IL or missing references)
			//IL_01a4: Unknown result type (might be due to invalid IL or missing references)
			//IL_01b4: Unknown result type (might be due to invalid IL or missing references)
			//IL_01be: Expected O, but got Unknown
			//IL_01c3: Expected O, but got Unknown
			//IL_01c4: Unknown result type (might be due to invalid IL or missing references)
			//IL_01c9: Unknown result type (might be due to invalid IL or missing references)
			//IL_01ca: Unknown result type (might be due to invalid IL or missing references)
			//IL_01f9: Unknown result type (might be due to invalid IL or missing references)
			//IL_020d: Expected O, but got Unknown
			//IL_020e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0213: Unknown result type (might be due to invalid IL or missing references)
			//IL_0216: Expected O, but got Unknown
			//IL_0230: Unknown result type (might be due to invalid IL or missing references)
			//IL_0235: Unknown result type (might be due to invalid IL or missing references)
			//IL_0268: Expected O, but got Unknown
			//IL_0269: Unknown result type (might be due to invalid IL or missing references)
			//IL_026e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0271: Expected O, but got Unknown
			//IL_028b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0290: Unknown result type (might be due to invalid IL or missing references)
			//IL_02c3: Expected O, but got Unknown
			//IL_02c4: Unknown result type (might be due to invalid IL or missing references)
			//IL_02c9: Unknown result type (might be due to invalid IL or missing references)
			//IL_02ca: Unknown result type (might be due to invalid IL or missing references)
			//IL_02d7: Unknown result type (might be due to invalid IL or missing references)
			//IL_02e1: Expected O, but got Unknown
			//IL_02e6: Expected O, but got Unknown
			//IL_02e6: Unknown result type (might be due to invalid IL or missing references)
			//IL_02eb: Unknown result type (might be due to invalid IL or missing references)
			//IL_02ec: Unknown result type (might be due to invalid IL or missing references)
			//IL_02f7: Unknown result type (might be due to invalid IL or missing references)
			//IL_02fc: Unknown result type (might be due to invalid IL or missing references)
			//IL_0313: Expected O, but got Unknown
			//IL_0318: Expected O, but got Unknown
			//IL_0323: Expected O, but got Unknown
			//IL_0329: Unknown result type (might be due to invalid IL or missing references)
			//IL_032e: Unknown result type (might be due to invalid IL or missing references)
			//IL_032f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0359: Unknown result type (might be due to invalid IL or missing references)
			//IL_035e: Unknown result type (might be due to invalid IL or missing references)
			//IL_035f: Unknown result type (might be due to invalid IL or missing references)
			//IL_036f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0379: Expected O, but got Unknown
			//IL_037e: Expected O, but got Unknown
			//IL_0383: Expected O, but got Unknown
			//IL_0389: Unknown result type (might be due to invalid IL or missing references)
			//IL_038e: Unknown result type (might be due to invalid IL or missing references)
			//IL_038f: Unknown result type (might be due to invalid IL or missing references)
			//IL_03b9: Unknown result type (might be due to invalid IL or missing references)
			//IL_03be: Unknown result type (might be due to invalid IL or missing references)
			//IL_03bf: Unknown result type (might be due to invalid IL or missing references)
			//IL_03cf: Unknown result type (might be due to invalid IL or missing references)
			//IL_03d9: Expected O, but got Unknown
			//IL_03de: Expected O, but got Unknown
			//IL_03e3: Expected O, but got Unknown
			//IL_03e9: Unknown result type (might be due to invalid IL or missing references)
			//IL_03ee: Unknown result type (might be due to invalid IL or missing references)
			//IL_03f1: Expected O, but got Unknown
			//IL_041c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0421: Unknown result type (might be due to invalid IL or missing references)
			//IL_0422: Unknown result type (might be due to invalid IL or missing references)
			//IL_0432: Unknown result type (might be due to invalid IL or missing references)
			//IL_043c: Expected O, but got Unknown
			//IL_0441: Expected O, but got Unknown
			//IL_0442: Unknown result type (might be due to invalid IL or missing references)
			//IL_0447: Unknown result type (might be due to invalid IL or missing references)
			//IL_0448: Unknown result type (might be due to invalid IL or missing references)
			//IL_0458: Unknown result type (might be due to invalid IL or missing references)
			//IL_0462: Expected O, but got Unknown
			//IL_0467: Expected O, but got Unknown
			//IL_0468: Unknown result type (might be due to invalid IL or missing references)
			//IL_046d: Unknown result type (might be due to invalid IL or missing references)
			//IL_046e: Unknown result type (might be due to invalid IL or missing references)
			//IL_047e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0488: Expected O, but got Unknown
			//IL_048d: Expected O, but got Unknown
			//IL_048e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0493: Unknown result type (might be due to invalid IL or missing references)
			//IL_0494: Unknown result type (might be due to invalid IL or missing references)
			//IL_04c3: Unknown result type (might be due to invalid IL or missing references)
			//IL_04d7: Expected O, but got Unknown
			//IL_04d8: Unknown result type (might be due to invalid IL or missing references)
			//IL_04dd: Unknown result type (might be due to invalid IL or missing references)
			//IL_04e0: Expected O, but got Unknown
			//IL_04fa: Unknown result type (might be due to invalid IL or missing references)
			//IL_04ff: Unknown result type (might be due to invalid IL or missing references)
			//IL_0532: Expected O, but got Unknown
			//IL_0533: Unknown result type (might be due to invalid IL or missing references)
			//IL_0538: Unknown result type (might be due to invalid IL or missing references)
			//IL_053b: Expected O, but got Unknown
			//IL_0555: Unknown result type (might be due to invalid IL or missing references)
			//IL_055a: Unknown result type (might be due to invalid IL or missing references)
			//IL_058d: Expected O, but got Unknown
			//IL_058d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0592: Unknown result type (might be due to invalid IL or missing references)
			//IL_0593: Unknown result type (might be due to invalid IL or missing references)
			//IL_059e: Unknown result type (might be due to invalid IL or missing references)
			//IL_05a3: Unknown result type (might be due to invalid IL or missing references)
			//IL_05ba: Expected O, but got Unknown
			//IL_05bf: Expected O, but got Unknown
			//IL_05ca: Expected O, but got Unknown
			//IL_05d0: Unknown result type (might be due to invalid IL or missing references)
			//IL_05d5: Unknown result type (might be due to invalid IL or missing references)
			//IL_05d6: Unknown result type (might be due to invalid IL or missing references)
			//IL_0600: Unknown result type (might be due to invalid IL or missing references)
			//IL_0601: Unknown result type (might be due to invalid IL or missing references)
			//IL_0606: Unknown result type (might be due to invalid IL or missing references)
			//IL_0607: Unknown result type (might be due to invalid IL or missing references)
			//IL_0617: Unknown result type (might be due to invalid IL or missing references)
			//IL_0621: Expected O, but got Unknown
			//IL_0626: Expected O, but got Unknown
			//IL_0626: Unknown result type (might be due to invalid IL or missing references)
			//IL_062b: Unknown result type (might be due to invalid IL or missing references)
			//IL_062c: Unknown result type (might be due to invalid IL or missing references)
			//IL_063c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0646: Expected O, but got Unknown
			//IL_064b: Expected O, but got Unknown
			//IL_0650: Expected O, but got Unknown
			//IL_0655: Unknown result type (might be due to invalid IL or missing references)
			//IL_065a: Unknown result type (might be due to invalid IL or missing references)
			//IL_065b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0685: Unknown result type (might be due to invalid IL or missing references)
			//IL_068a: Unknown result type (might be due to invalid IL or missing references)
			//IL_068b: Unknown result type (might be due to invalid IL or missing references)
			//IL_069b: Unknown result type (might be due to invalid IL or missing references)
			//IL_06a5: Expected O, but got Unknown
			//IL_06aa: Expected O, but got Unknown
			//IL_06af: Expected O, but got Unknown
			//IL_06b0: Unknown result type (might be due to invalid IL or missing references)
			//IL_06b5: Unknown result type (might be due to invalid IL or missing references)
			//IL_06b8: Expected O, but got Unknown
			//IL_06b8: Unknown result type (might be due to invalid IL or missing references)
			//IL_06be: Expected O, but got Unknown
			//IL_06c3: Expected O, but got Unknown
			//IL_06da: Unknown result type (might be due to invalid IL or missing references)
			//IL_06df: Unknown result type (might be due to invalid IL or missing references)
			//IL_06e2: Expected O, but got Unknown
			//IL_06e2: Unknown result type (might be due to invalid IL or missing references)
			//IL_06e8: Expected O, but got Unknown
			//IL_06ed: Expected O, but got Unknown
			//IL_0726: Unknown result type (might be due to invalid IL or missing references)
			//IL_072b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0756: Unknown result type (might be due to invalid IL or missing references)
			//IL_075b: Unknown result type (might be due to invalid IL or missing references)
			//IL_075e: Expected O, but got Unknown
			//IL_075e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0764: Expected O, but got Unknown
			//IL_0769: Expected O, but got Unknown
			//IL_07a7: Unknown result type (might be due to invalid IL or missing references)
			//IL_07ac: Unknown result type (might be due to invalid IL or missing references)
			//IL_07af: Expected O, but got Unknown
			//IL_07af: Unknown result type (might be due to invalid IL or missing references)
			//IL_07b5: Expected O, but got Unknown
			//IL_07ba: Expected O, but got Unknown
			//IL_07ea: Unknown result type (might be due to invalid IL or missing references)
			//IL_07f4: Expected O, but got Unknown
			//IL_07ff: Unknown result type (might be due to invalid IL or missing references)
			//IL_0804: Unknown result type (might be due to invalid IL or missing references)
			//IL_0844: Unknown result type (might be due to invalid IL or missing references)
			//IL_0849: Unknown result type (might be due to invalid IL or missing references)
			//IL_084c: Expected O, but got Unknown
			//IL_084c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0852: Expected O, but got Unknown
			//IL_0857: Expected O, but got Unknown
			//IL_0868: Unknown result type (might be due to invalid IL or missing references)
			//IL_086d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0875: Expected O, but got Unknown
			//IL_0875: Unknown result type (might be due to invalid IL or missing references)
			//IL_0880: Unknown result type (might be due to invalid IL or missing references)
			//IL_0885: Unknown result type (might be due to invalid IL or missing references)
			//IL_088f: Expected O, but got Unknown
			//IL_088f: Expected O, but got Unknown
			//IL_088f: Unknown result type (might be due to invalid IL or missing references)
			//IL_089a: Unknown result type (might be due to invalid IL or missing references)
			//IL_089f: Unknown result type (might be due to invalid IL or missing references)
			//IL_08a9: Expected O, but got Unknown
			//IL_08a9: Expected O, but got Unknown
			//IL_08a9: Unknown result type (might be due to invalid IL or missing references)
			//IL_08b4: Unknown result type (might be due to invalid IL or missing references)
			//IL_08b9: Unknown result type (might be due to invalid IL or missing references)
			//IL_08c3: Expected O, but got Unknown
			//IL_08c3: Expected O, but got Unknown
			//IL_08c3: Unknown result type (might be due to invalid IL or missing references)
			//IL_08ce: Unknown result type (might be due to invalid IL or missing references)
			//IL_08d3: Unknown result type (might be due to invalid IL or missing references)
			//IL_08dd: Expected O, but got Unknown
			//IL_08dd: Expected O, but got Unknown
			//IL_08dd: Unknown result type (might be due to invalid IL or missing references)
			//IL_08e8: Unknown result type (might be due to invalid IL or missing references)
			//IL_08ed: Unknown result type (might be due to invalid IL or missing references)
			//IL_08f7: Expected O, but got Unknown
			//IL_08f7: Expected O, but got Unknown
			//IL_08f7: Unknown result type (might be due to invalid IL or missing references)
			//IL_0902: Unknown result type (might be due to invalid IL or missing references)
			//IL_0907: Unknown result type (might be due to invalid IL or missing references)
			//IL_0911: Expected O, but got Unknown
			//IL_0911: Expected O, but got Unknown
			//IL_0911: Unknown result type (might be due to invalid IL or missing references)
			//IL_091c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0921: Unknown result type (might be due to invalid IL or missing references)
			//IL_092b: Expected O, but got Unknown
			//IL_092b: Expected O, but got Unknown
			//IL_092b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0936: Unknown result type (might be due to invalid IL or missing references)
			//IL_093b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0945: Expected O, but got Unknown
			//IL_0945: Expected O, but got Unknown
			//IL_0945: Unknown result type (might be due to invalid IL or missing references)
			//IL_0950: Unknown result type (might be due to invalid IL or missing references)
			//IL_0955: Unknown result type (might be due to invalid IL or missing references)
			//IL_095f: Expected O, but got Unknown
			//IL_095f: Expected O, but got Unknown
			//IL_0964: Expected O, but got Unknown
			//IL_096b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0970: Unknown result type (might be due to invalid IL or missing references)
			//IL_0973: Expected O, but got Unknown
			//IL_0973: Unknown result type (might be due to invalid IL or missing references)
			//IL_0979: Expected O, but got Unknown
			//IL_097e: Expected O, but got Unknown
			//IL_09d8: Unknown result type (might be due to invalid IL or missing references)
			//IL_0a13: Unknown result type (might be due to invalid IL or missing references)
			//IL_0a18: Unknown result type (might be due to invalid IL or missing references)
			//IL_0a1b: Expected O, but got Unknown
			//IL_0a1b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0a21: Expected O, but got Unknown
			//IL_0a26: Expected O, but got Unknown
			//IL_0a5f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0a64: Unknown result type (might be due to invalid IL or missing references)
			//IL_0a99: Unknown result type (might be due to invalid IL or missing references)
			//IL_0a9e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0ad5: Unknown result type (might be due to invalid IL or missing references)
			//IL_0adf: Expected O, but got Unknown
			//IL_0ae4: Unknown result type (might be due to invalid IL or missing references)
			//IL_0ae9: Unknown result type (might be due to invalid IL or missing references)
			//IL_0b25: Unknown result type (might be due to invalid IL or missing references)
			//IL_0b2a: Unknown result type (might be due to invalid IL or missing references)
			//IL_0b2d: Expected O, but got Unknown
			//IL_0b2d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0b33: Expected O, but got Unknown
			//IL_0b38: Expected O, but got Unknown
			//IL_0b92: Unknown result type (might be due to invalid IL or missing references)
			//IL_0bcd: Unknown result type (might be due to invalid IL or missing references)
			//IL_0bd2: Unknown result type (might be due to invalid IL or missing references)
			//IL_0bd5: Expected O, but got Unknown
			//IL_0bd5: Unknown result type (might be due to invalid IL or missing references)
			//IL_0bdb: Expected O, but got Unknown
			//IL_0be0: Expected O, but got Unknown
			//IL_0c19: Unknown result type (might be due to invalid IL or missing references)
			//IL_0c1e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0c53: Unknown result type (might be due to invalid IL or missing references)
			//IL_0c58: Unknown result type (might be due to invalid IL or missing references)
			//IL_0c8f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0c99: Expected O, but got Unknown
			//IL_0c9e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0ca3: Unknown result type (might be due to invalid IL or missing references)
			//IL_0cdf: Unknown result type (might be due to invalid IL or missing references)
			//IL_0ce4: Unknown result type (might be due to invalid IL or missing references)
			//IL_0ce7: Expected O, but got Unknown
			//IL_0ce7: Unknown result type (might be due to invalid IL or missing references)
			//IL_0ced: Expected O, but got Unknown
			//IL_0cf2: Expected O, but got Unknown
			//IL_0d4c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0d87: Unknown result type (might be due to invalid IL or missing references)
			//IL_0d8c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0d8f: Expected O, but got Unknown
			//IL_0d8f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0d95: Expected O, but got Unknown
			//IL_0d9a: Expected O, but got Unknown
			//IL_0dd3: Unknown result type (might be due to invalid IL or missing references)
			//IL_0dd8: Unknown result type (might be due to invalid IL or missing references)
			//IL_0e0d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0e12: Unknown result type (might be due to invalid IL or missing references)
			//IL_0e49: Unknown result type (might be due to invalid IL or missing references)
			//IL_0e53: Expected O, but got Unknown
			//IL_0e58: Unknown result type (might be due to invalid IL or missing references)
			//IL_0e5d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0e99: Unknown result type (might be due to invalid IL or missing references)
			//IL_0e9e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0ea1: Expected O, but got Unknown
			//IL_0ea1: Unknown result type (might be due to invalid IL or missing references)
			//IL_0ea7: Expected O, but got Unknown
			//IL_0eac: Expected O, but got Unknown
			//IL_0f18: Unknown result type (might be due to invalid IL or missing references)
			//IL_0f2c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0f31: Unknown result type (might be due to invalid IL or missing references)
			//IL_0f6d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0f72: Unknown result type (might be due to invalid IL or missing references)
			//IL_0f75: Expected O, but got Unknown
			//IL_0f75: Unknown result type (might be due to invalid IL or missing references)
			//IL_0f7b: Expected O, but got Unknown
			//IL_0f80: Expected O, but got Unknown
			//IL_1010: Unknown result type (might be due to invalid IL or missing references)
			//IL_1026: Unknown result type (might be due to invalid IL or missing references)
			//IL_102b: Unknown result type (might be due to invalid IL or missing references)
			//IL_102e: Expected O, but got Unknown
			//IL_102e: Unknown result type (might be due to invalid IL or missing references)
			//IL_1034: Expected O, but got Unknown
			//IL_1039: Expected O, but got Unknown
			//IL_10a5: Unknown result type (might be due to invalid IL or missing references)
			//IL_10b9: Unknown result type (might be due to invalid IL or missing references)
			//IL_10be: Unknown result type (might be due to invalid IL or missing references)
			//IL_10fa: Unknown result type (might be due to invalid IL or missing references)
			//IL_10ff: Unknown result type (might be due to invalid IL or missing references)
			//IL_1102: Expected O, but got Unknown
			//IL_1102: Unknown result type (might be due to invalid IL or missing references)
			//IL_1108: Expected O, but got Unknown
			//IL_110d: Expected O, but got Unknown
			//IL_1167: Unknown result type (might be due to invalid IL or missing references)
			//IL_11a2: Unknown result type (might be due to invalid IL or missing references)
			//IL_11a7: Unknown result type (might be due to invalid IL or missing references)
			//IL_11aa: Expected O, but got Unknown
			//IL_11aa: Unknown result type (might be due to invalid IL or missing references)
			//IL_11b0: Expected O, but got Unknown
			//IL_11b5: Expected O, but got Unknown
			//IL_11ee: Unknown result type (might be due to invalid IL or missing references)
			//IL_11f3: Unknown result type (might be due to invalid IL or missing references)
			//IL_1228: Unknown result type (might be due to invalid IL or missing references)
			//IL_122d: Unknown result type (might be due to invalid IL or missing references)
			//IL_1264: Unknown result type (might be due to invalid IL or missing references)
			//IL_126e: Expected O, but got Unknown
			//IL_1273: Unknown result type (might be due to invalid IL or missing references)
			//IL_1278: Unknown result type (might be due to invalid IL or missing references)
			//IL_12b4: Unknown result type (might be due to invalid IL or missing references)
			//IL_12b9: Unknown result type (might be due to invalid IL or missing references)
			//IL_12bc: Expected O, but got Unknown
			//IL_12bc: Unknown result type (might be due to invalid IL or missing references)
			//IL_12c2: Expected O, but got Unknown
			//IL_12c7: Expected O, but got Unknown
			//IL_134c: Unknown result type (might be due to invalid IL or missing references)
			//IL_1351: Unknown result type (might be due to invalid IL or missing references)
			//IL_1354: Expected O, but got Unknown
			//IL_1354: Unknown result type (might be due to invalid IL or missing references)
			//IL_135a: Expected O, but got Unknown
			//IL_135f: Expected O, but got Unknown
			//IL_13b9: Unknown result type (might be due to invalid IL or missing references)
			//IL_13f4: Unknown result type (might be due to invalid IL or missing references)
			//IL_13f9: Unknown result type (might be due to invalid IL or missing references)
			//IL_13fc: Expected O, but got Unknown
			//IL_13fc: Unknown result type (might be due to invalid IL or missing references)
			//IL_1402: Expected O, but got Unknown
			//IL_1407: Expected O, but got Unknown
			//IL_1440: Unknown result type (might be due to invalid IL or missing references)
			//IL_1445: Unknown result type (might be due to invalid IL or missing references)
			//IL_147a: Unknown result type (might be due to invalid IL or missing references)
			//IL_147f: Unknown result type (might be due to invalid IL or missing references)
			//IL_14b6: Unknown result type (might be due to invalid IL or missing references)
			//IL_14c0: Expected O, but got Unknown
			//IL_14c5: Unknown result type (might be due to invalid IL or missing references)
			//IL_14ca: Unknown result type (might be due to invalid IL or missing references)
			XamlIlContext.Context<VideoPlayerControl> context = new XamlIlContext.Context<VideoPlayerControl>(P_0, new object[1] { !AvaloniaResources.NamespaceInfo:/VideoPlayerControl.axaml.Singleton }, "avares://Avalonia.FFmpegVideoPlayer/VideoPlayerControl.axaml")
			{
				RootObject = P_1,
				IntermediateRoot = P_1
			};
			((ISupportInitialize)P_1).BeginInit();
			context.PushParent(P_1);
			IResourceDictionary resources = ((StyledElement)P_1).Resources;
			ResourceDictionary val = (ResourceDictionary)(object)((resources is ResourceDictionary) ? resources : null);
			if (val != null)
			{
				val.EnsureCapacity(val.Count + 8);
			}
			((StyledElement)P_1).Name = "Root";
			object obj = P_1;
			context.AvaloniaNameScope.Register("Root", obj);
			((Visual)P_1).ClipToBounds = true;
			((ResourceDictionary)((StyledElement)P_1).Resources).AddDeferred((object)"PlayIcon", XamlIlRuntimeHelpers.DeferredTransformationFactoryV3<object>((IntPtr)(nint)(delegate*<IServiceProvider?, object?>)(&XamlClosure_1.Build_1), (IServiceProvider)context));
			((ResourceDictionary)((StyledElement)P_1).Resources).AddDeferred((object)"PauseIcon", XamlIlRuntimeHelpers.DeferredTransformationFactoryV3<object>((IntPtr)(nint)(delegate*<IServiceProvider?, object?>)(&XamlClosure_1.Build_2), (IServiceProvider)context));
			((ResourceDictionary)((StyledElement)P_1).Resources).AddDeferred((object)"StopIcon", XamlIlRuntimeHelpers.DeferredTransformationFactoryV3<object>((IntPtr)(nint)(delegate*<IServiceProvider?, object?>)(&XamlClosure_1.Build_3), (IServiceProvider)context));
			((ResourceDictionary)((StyledElement)P_1).Resources).AddDeferred((object)"FolderOpenIcon", XamlIlRuntimeHelpers.DeferredTransformationFactoryV3<object>((IntPtr)(nint)(delegate*<IServiceProvider?, object?>)(&XamlClosure_1.Build_4), (IServiceProvider)context));
			((ResourceDictionary)((StyledElement)P_1).Resources).AddDeferred((object)"VolumeHighIcon", XamlIlRuntimeHelpers.DeferredTransformationFactoryV3<object>((IntPtr)(nint)(delegate*<IServiceProvider?, object?>)(&XamlClosure_1.Build_5), (IServiceProvider)context));
			((ResourceDictionary)((StyledElement)P_1).Resources).AddDeferred((object)"VolumeOffIcon", XamlIlRuntimeHelpers.DeferredTransformationFactoryV3<object>((IntPtr)(nint)(delegate*<IServiceProvider?, object?>)(&XamlClosure_1.Build_6), (IServiceProvider)context));
			((ResourceDictionary)((StyledElement)P_1).Resources).AddDeferred((object)"FullscreenIcon", XamlIlRuntimeHelpers.DeferredTransformationFactoryV3<object>((IntPtr)(nint)(delegate*<IServiceProvider?, object?>)(&XamlClosure_1.Build_7), (IServiceProvider)context));
			((ResourceDictionary)((StyledElement)P_1).Resources).AddDeferred((object)"FullscreenExitIcon", XamlIlRuntimeHelpers.DeferredTransformationFactoryV3<object>((IntPtr)(nint)(delegate*<IServiceProvider?, object?>)(&XamlClosure_1.Build_8), (IServiceProvider)context));
			Styles styles = ((StyledElement)P_1).Styles;
			Style val2 = new Style();
			Style val3 = val2;
			context.PushParent(val3);
			Style obj2 = val3;
			obj2.Selector = Selectors.Class(Selectors.OfType((Selector)null, typeof(Button)), "iconButton");
			Setter val4 = new Setter();
			val4.Property = (AvaloniaProperty)(object)TemplatedControl.BackgroundProperty;
			val4.Value = (object)new ImmutableSolidColorBrush(16777215u);
			((StyleBase)obj2).Add((SetterBase)val4);
			Setter val5 = new Setter();
			val5.Property = (AvaloniaProperty)(object)TemplatedControl.BorderThicknessProperty;
			val5.Value = (object)new Thickness(0.0, 0.0, 0.0, 0.0);
			((StyleBase)obj2).Add((SetterBase)val5);
			Setter val6 = new Setter();
			Setter val7 = val6;
			context.PushParent(val7);
			Setter obj3 = val7;
			obj3.Property = (AvaloniaProperty)(object)TemplatedControl.PaddingProperty;
			ReflectionBindingExtension val8 = new ReflectionBindingExtension("ButtonPadding")
			{
				ElementName = "Root"
			};
			context.ProvideTargetProperty = XamlIlHelpers.Avalonia.Styling.Setter,Avalonia.Base.Value!Property();
			Binding value = val8.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			obj3.Value = value;
			context.PopParent();
			((StyleBase)obj2).Add((SetterBase)val6);
			Setter val9 = new Setter();
			val7 = val9;
			context.PushParent(val7);
			Setter obj4 = val7;
			obj4.Property = (AvaloniaProperty)(object)TemplatedControl.CornerRadiusProperty;
			ReflectionBindingExtension val10 = new ReflectionBindingExtension("ButtonCornerRadius")
			{
				ElementName = "Root"
			};
			context.ProvideTargetProperty = XamlIlHelpers.Avalonia.Styling.Setter,Avalonia.Base.Value!Property();
			Binding value2 = val10.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			obj4.Value = value2;
			context.PopParent();
			((StyleBase)obj2).Add((SetterBase)val9);
			Setter val11 = new Setter();
			val11.Property = (AvaloniaProperty)(object)InputElement.CursorProperty;
			val11.Value = (object)new Cursor((StandardCursorType)9);
			((StyleBase)obj2).Add((SetterBase)val11);
			Setter val12 = new Setter();
			val12.Property = (AvaloniaProperty)(object)TemplatedControl.TemplateProperty;
			val12.Value = (object)new ControlTemplate
			{
				Content = XamlIlRuntimeHelpers.DeferredTransformationFactoryV3<Control>((IntPtr)(nint)(delegate*<IServiceProvider?, object?>)(&XamlClosure_1.Build_9), (IServiceProvider)context)
			};
			((StyleBase)obj2).Add((SetterBase)val12);
			context.PopParent();
			styles.Add((IStyle)val2);
			Styles styles2 = ((StyledElement)P_1).Styles;
			Style val13 = new Style();
			val13.Selector = Selectors.Class(Selectors.Class(Selectors.OfType((Selector)null, typeof(Button)), "iconButton"), ":pointerover");
			Setter val14 = new Setter();
			val14.Property = (AvaloniaProperty)(object)TemplatedControl.BackgroundProperty;
			val14.Value = (object)new ImmutableSolidColorBrush(402653184u);
			((StyleBase)val13).Add((SetterBase)val14);
			styles2.Add((IStyle)val13);
			Styles styles3 = ((StyledElement)P_1).Styles;
			Style val15 = new Style();
			val15.Selector = Selectors.Class(Selectors.Class(Selectors.OfType((Selector)null, typeof(Button)), "iconButton"), ":pressed");
			Setter val16 = new Setter();
			val16.Property = (AvaloniaProperty)(object)TemplatedControl.BackgroundProperty;
			val16.Value = (object)new ImmutableSolidColorBrush(671088640u);
			((StyleBase)val15).Add((SetterBase)val16);
			styles3.Add((IStyle)val15);
			Styles styles4 = ((StyledElement)P_1).Styles;
			Style val17 = new Style();
			val3 = val17;
			context.PushParent(val3);
			Style obj5 = val3;
			obj5.Selector = Selectors.Class(Selectors.OfType((Selector)null, typeof(Button)), "mediaButton");
			Setter val18 = new Setter();
			val18.Property = (AvaloniaProperty)(object)TemplatedControl.BackgroundProperty;
			val18.Value = (object)new ImmutableSolidColorBrush(4293454056u);
			((StyleBase)obj5).Add((SetterBase)val18);
			Setter val19 = new Setter();
			val19.Property = (AvaloniaProperty)(object)TemplatedControl.ForegroundProperty;
			val19.Value = (object)new ImmutableSolidColorBrush(4281545523u);
			((StyleBase)obj5).Add((SetterBase)val19);
			Setter val20 = new Setter();
			val20.Property = (AvaloniaProperty)(object)TemplatedControl.BorderBrushProperty;
			val20.Value = (object)new ImmutableSolidColorBrush(4288256409u);
			((StyleBase)obj5).Add((SetterBase)val20);
			Setter val21 = new Setter();
			val21.Property = (AvaloniaProperty)(object)TemplatedControl.BorderThicknessProperty;
			val21.Value = (object)new Thickness(1.0, 1.0, 1.0, 1.0);
			((StyleBase)obj5).Add((SetterBase)val21);
			Setter val22 = new Setter();
			val7 = val22;
			context.PushParent(val7);
			Setter obj6 = val7;
			obj6.Property = (AvaloniaProperty)(object)TemplatedControl.PaddingProperty;
			ReflectionBindingExtension val23 = new ReflectionBindingExtension("ButtonPadding")
			{
				ElementName = "Root"
			};
			context.ProvideTargetProperty = XamlIlHelpers.Avalonia.Styling.Setter,Avalonia.Base.Value!Property();
			Binding value3 = val23.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			obj6.Value = value3;
			context.PopParent();
			((StyleBase)obj5).Add((SetterBase)val22);
			Setter val24 = new Setter();
			val7 = val24;
			context.PushParent(val7);
			Setter obj7 = val7;
			obj7.Property = (AvaloniaProperty)(object)TemplatedControl.CornerRadiusProperty;
			ReflectionBindingExtension val25 = new ReflectionBindingExtension("ButtonCornerRadius")
			{
				ElementName = "Root"
			};
			context.ProvideTargetProperty = XamlIlHelpers.Avalonia.Styling.Setter,Avalonia.Base.Value!Property();
			Binding value4 = val25.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			obj7.Value = value4;
			context.PopParent();
			((StyleBase)obj5).Add((SetterBase)val24);
			Setter val26 = new Setter();
			val26.Property = (AvaloniaProperty)(object)TemplatedControl.TemplateProperty;
			val26.Value = (object)new ControlTemplate
			{
				Content = XamlIlRuntimeHelpers.DeferredTransformationFactoryV3<Control>((IntPtr)(nint)(delegate*<IServiceProvider?, object?>)(&XamlClosure_1.Build_10), (IServiceProvider)context)
			};
			((StyleBase)obj5).Add((SetterBase)val26);
			context.PopParent();
			styles4.Add((IStyle)val17);
			Styles styles5 = ((StyledElement)P_1).Styles;
			Style val27 = new Style();
			val27.Selector = Selectors.Class(Selectors.Class(Selectors.OfType((Selector)null, typeof(Button)), "mediaButton"), ":pointerover");
			Setter val28 = new Setter();
			val28.Property = (AvaloniaProperty)(object)TemplatedControl.BackgroundProperty;
			val28.Value = (object)new ImmutableSolidColorBrush(4291875024u);
			((StyleBase)val27).Add((SetterBase)val28);
			Setter val29 = new Setter();
			val29.Property = (AvaloniaProperty)(object)TemplatedControl.BorderBrushProperty;
			val29.Value = (object)new ImmutableSolidColorBrush(4284900966u);
			((StyleBase)val27).Add((SetterBase)val29);
			styles5.Add((IStyle)val27);
			Styles styles6 = ((StyledElement)P_1).Styles;
			Style val30 = new Style();
			val30.Selector = Selectors.Class(Selectors.Class(Selectors.OfType((Selector)null, typeof(Button)), "mediaButton"), ":pressed");
			Setter val31 = new Setter();
			val31.Property = (AvaloniaProperty)(object)TemplatedControl.BackgroundProperty;
			val31.Value = (object)new ImmutableSolidColorBrush(4290822336u);
			((StyleBase)val30).Add((SetterBase)val31);
			styles6.Add((IStyle)val30);
			Grid val32 = new Grid();
			Grid val33 = val32;
			((ISupportInitialize)val32).BeginInit();
			((ContentControl)P_1).Content = (object)val32;
			Grid val34;
			Grid obj8 = (val34 = val33);
			context.PushParent(val34);
			Grid obj9 = val34;
			Controls children = ((Panel)obj9).Children;
			Border val35 = new Border();
			Border val36 = val35;
			((ISupportInitialize)val35).BeginInit();
			((AvaloniaList<Control>)(object)children).Add((Control)val35);
			Border val37;
			Border obj10 = (val37 = val36);
			context.PushParent(val37);
			Border obj11 = val37;
			((StyledElement)obj11).Name = "VideoBorder";
			obj = obj11;
			context.AvaloniaNameScope.Register("VideoBorder", obj);
			StyledProperty<IBrush> backgroundProperty = Border.BackgroundProperty;
			ReflectionBindingExtension val38 = new ReflectionBindingExtension("VideoBackground")
			{
				ElementName = "Root"
			};
			context.ProvideTargetProperty = Border.BackgroundProperty;
			Binding obj12 = val38.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)obj11, (AvaloniaProperty)(object)backgroundProperty, (IBinding)(object)obj12, (object)null);
			Image val39 = new Image();
			Image val40 = val39;
			((ISupportInitialize)val39).BeginInit();
			((Decorator)obj11).Child = (Control)val39;
			((StyledElement)val40).Name = "VideoImage";
			obj = val40;
			context.AvaloniaNameScope.Register("VideoImage", obj);
			val40.Stretch = (Stretch)2;
			((ISupportInitialize)val40).EndInit();
			context.PopParent();
			((ISupportInitialize)obj10).EndInit();
			Controls children2 = ((Panel)obj9).Children;
			Border val41 = new Border();
			Border val42 = val41;
			((ISupportInitialize)val41).BeginInit();
			((AvaloniaList<Control>)(object)children2).Add((Control)val41);
			Border obj13 = (val37 = val42);
			context.PushParent(val37);
			Border obj14 = val37;
			((StyledElement)obj14).Name = "ControlPanelBorder";
			obj = obj14;
			context.AvaloniaNameScope.Register("ControlPanelBorder", obj);
			obj14.Background = (IBrush)new ImmutableSolidColorBrush(uint.MaxValue);
			StyledProperty<Thickness> paddingProperty = Decorator.PaddingProperty;
			ReflectionBindingExtension val43 = new ReflectionBindingExtension("ControlPanelPadding")
			{
				ElementName = "Root"
			};
			context.ProvideTargetProperty = Decorator.PaddingProperty;
			Binding obj15 = val43.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)obj14, (AvaloniaProperty)(object)paddingProperty, (IBinding)(object)obj15, (object)null);
			((Layoutable)obj14).VerticalAlignment = (VerticalAlignment)3;
			((Layoutable)obj14).HorizontalAlignment = (HorizontalAlignment)0;
			((Visual)obj14).ClipToBounds = true;
			Grid val44 = new Grid();
			Grid val45 = val44;
			((ISupportInitialize)val44).BeginInit();
			((Decorator)obj14).Child = (Control)val44;
			Grid obj16 = (val34 = val45);
			context.PushParent(val34);
			Grid obj17 = val34;
			ColumnDefinitions val46 = new ColumnDefinitions();
			((AvaloniaList<ColumnDefinition>)val46).Capacity = 9;
			((AvaloniaList<ColumnDefinition>)val46).Add(new ColumnDefinition(new GridLength(0.0, (GridUnitType)0)));
			((AvaloniaList<ColumnDefinition>)val46).Add(new ColumnDefinition(new GridLength(0.0, (GridUnitType)0)));
			((AvaloniaList<ColumnDefinition>)val46).Add(new ColumnDefinition(new GridLength(0.0, (GridUnitType)0)));
			((AvaloniaList<ColumnDefinition>)val46).Add(new ColumnDefinition(new GridLength(0.0, (GridUnitType)0)));
			((AvaloniaList<ColumnDefinition>)val46).Add(new ColumnDefinition(new GridLength(1.0, (GridUnitType)2)));
			((AvaloniaList<ColumnDefinition>)val46).Add(new ColumnDefinition(new GridLength(0.0, (GridUnitType)0)));
			((AvaloniaList<ColumnDefinition>)val46).Add(new ColumnDefinition(new GridLength(0.0, (GridUnitType)0)));
			((AvaloniaList<ColumnDefinition>)val46).Add(new ColumnDefinition(new GridLength(0.0, (GridUnitType)0)));
			((AvaloniaList<ColumnDefinition>)val46).Add(new ColumnDefinition(new GridLength(0.0, (GridUnitType)0)));
			obj17.ColumnDefinitions = val46;
			Controls children3 = ((Panel)obj17).Children;
			Button val47 = new Button();
			Button val48 = val47;
			((ISupportInitialize)val47).BeginInit();
			((AvaloniaList<Control>)(object)children3).Add((Control)val47);
			Button val49;
			Button obj18 = (val49 = val48);
			context.PushParent(val49);
			Button obj19 = val49;
			((StyledElement)obj19).Name = "OpenButton";
			obj = obj19;
			context.AvaloniaNameScope.Register("OpenButton", obj);
			Grid.SetColumn((Control)(object)obj19, 0);
			((Layoutable)obj19).Margin = new Thickness(0.0, 0.0, 2.0, 0.0);
			((StyledElement)obj19).Classes.Add("iconButton");
			((Interactive)obj19).AddHandler((RoutedEvent)(object)val49.ClickEvent, (Delegate)new EventHandler<RoutedEventArgs>(context.RootObject.OnOpenClick), (RoutingStrategies)5, false);
			Path val50 = new Path();
			Path val51 = val50;
			((ISupportInitialize)val50).BeginInit();
			((ContentControl)obj19).Content = (object)val50;
			Path val52;
			Path obj20 = (val52 = val51);
			context.PushParent(val52);
			Path obj21 = val52;
			((StyledElement)obj21).Name = "FolderIcon";
			obj = obj21;
			context.AvaloniaNameScope.Register("FolderIcon", obj);
			StyledProperty<double> widthProperty = Layoutable.WidthProperty;
			ReflectionBindingExtension val53 = new ReflectionBindingExtension("IconSize")
			{
				ElementName = "Root"
			};
			context.ProvideTargetProperty = Layoutable.WidthProperty;
			Binding obj22 = val53.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)obj21, (AvaloniaProperty)(object)widthProperty, (IBinding)(object)obj22, (object)null);
			StyledProperty<double> heightProperty = Layoutable.HeightProperty;
			ReflectionBindingExtension val54 = new ReflectionBindingExtension("IconSize")
			{
				ElementName = "Root"
			};
			context.ProvideTargetProperty = Layoutable.HeightProperty;
			Binding obj23 = val54.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)obj21, (AvaloniaProperty)(object)heightProperty, (IBinding)(object)obj23, (object)null);
			((Shape)obj21).Stretch = (Stretch)2;
			((Shape)obj21).Fill = (IBrush)new ImmutableSolidColorBrush(4281545523u);
			StaticResourceExtension val55 = new StaticResourceExtension((object)"FolderOpenIcon");
			context.ProvideTargetProperty = Path.DataProperty;
			object obj24 = val55.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			XamlDynamicSetters.<>XamlDynamicSetter_2(obj21, obj24);
			context.PopParent();
			((ISupportInitialize)obj20).EndInit();
			context.PopParent();
			((ISupportInitialize)obj18).EndInit();
			Controls children4 = ((Panel)obj17).Children;
			Button val56 = new Button();
			Button val57 = val56;
			((ISupportInitialize)val56).BeginInit();
			((AvaloniaList<Control>)(object)children4).Add((Control)val56);
			Button obj25 = (val49 = val57);
			context.PushParent(val49);
			Button obj26 = val49;
			((StyledElement)obj26).Name = "PlayPauseButton";
			obj = obj26;
			context.AvaloniaNameScope.Register("PlayPauseButton", obj);
			Grid.SetColumn((Control)(object)obj26, 1);
			((Layoutable)obj26).Margin = new Thickness(0.0, 0.0, 2.0, 0.0);
			((StyledElement)obj26).Classes.Add("iconButton");
			((Interactive)obj26).AddHandler((RoutedEvent)(object)val49.ClickEvent, (Delegate)new EventHandler<RoutedEventArgs>(context.RootObject.OnPlayPauseClick), (RoutingStrategies)5, false);
			Path val58 = new Path();
			Path val59 = val58;
			((ISupportInitialize)val58).BeginInit();
			((ContentControl)obj26).Content = (object)val58;
			Path obj27 = (val52 = val59);
			context.PushParent(val52);
			Path obj28 = val52;
			((StyledElement)obj28).Name = "PlayPauseIcon";
			obj = obj28;
			context.AvaloniaNameScope.Register("PlayPauseIcon", obj);
			StyledProperty<double> widthProperty2 = Layoutable.WidthProperty;
			ReflectionBindingExtension val60 = new ReflectionBindingExtension("IconSize")
			{
				ElementName = "Root"
			};
			context.ProvideTargetProperty = Layoutable.WidthProperty;
			Binding obj29 = val60.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)obj28, (AvaloniaProperty)(object)widthProperty2, (IBinding)(object)obj29, (object)null);
			StyledProperty<double> heightProperty2 = Layoutable.HeightProperty;
			ReflectionBindingExtension val61 = new ReflectionBindingExtension("IconSize")
			{
				ElementName = "Root"
			};
			context.ProvideTargetProperty = Layoutable.HeightProperty;
			Binding obj30 = val61.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)obj28, (AvaloniaProperty)(object)heightProperty2, (IBinding)(object)obj30, (object)null);
			((Shape)obj28).Stretch = (Stretch)2;
			((Shape)obj28).Fill = (IBrush)new ImmutableSolidColorBrush(4281545523u);
			StaticResourceExtension val62 = new StaticResourceExtension((object)"PlayIcon");
			context.ProvideTargetProperty = Path.DataProperty;
			object obj31 = val62.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			XamlDynamicSetters.<>XamlDynamicSetter_2(obj28, obj31);
			context.PopParent();
			((ISupportInitialize)obj27).EndInit();
			context.PopParent();
			((ISupportInitialize)obj25).EndInit();
			Controls children5 = ((Panel)obj17).Children;
			Button val63 = new Button();
			Button val64 = val63;
			((ISupportInitialize)val63).BeginInit();
			((AvaloniaList<Control>)(object)children5).Add((Control)val63);
			Button obj32 = (val49 = val64);
			context.PushParent(val49);
			Button obj33 = val49;
			((StyledElement)obj33).Name = "StopButton";
			obj = obj33;
			context.AvaloniaNameScope.Register("StopButton", obj);
			Grid.SetColumn((Control)(object)obj33, 2);
			((Layoutable)obj33).Margin = new Thickness(0.0, 0.0, 4.0, 0.0);
			((StyledElement)obj33).Classes.Add("iconButton");
			((Interactive)obj33).AddHandler((RoutedEvent)(object)val49.ClickEvent, (Delegate)new EventHandler<RoutedEventArgs>(context.RootObject.OnStopClick), (RoutingStrategies)5, false);
			Path val65 = new Path();
			Path val66 = val65;
			((ISupportInitialize)val65).BeginInit();
			((ContentControl)obj33).Content = (object)val65;
			Path obj34 = (val52 = val66);
			context.PushParent(val52);
			Path obj35 = val52;
			((StyledElement)obj35).Name = "StopIcon";
			obj = obj35;
			context.AvaloniaNameScope.Register("StopIcon", obj);
			StyledProperty<double> widthProperty3 = Layoutable.WidthProperty;
			ReflectionBindingExtension val67 = new ReflectionBindingExtension("IconSize")
			{
				ElementName = "Root"
			};
			context.ProvideTargetProperty = Layoutable.WidthProperty;
			Binding obj36 = val67.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)obj35, (AvaloniaProperty)(object)widthProperty3, (IBinding)(object)obj36, (object)null);
			StyledProperty<double> heightProperty3 = Layoutable.HeightProperty;
			ReflectionBindingExtension val68 = new ReflectionBindingExtension("IconSize")
			{
				ElementName = "Root"
			};
			context.ProvideTargetProperty = Layoutable.HeightProperty;
			Binding obj37 = val68.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)obj35, (AvaloniaProperty)(object)heightProperty3, (IBinding)(object)obj37, (object)null);
			((Shape)obj35).Stretch = (Stretch)2;
			((Shape)obj35).Fill = (IBrush)new ImmutableSolidColorBrush(4281545523u);
			StaticResourceExtension val69 = new StaticResourceExtension((object)"StopIcon");
			context.ProvideTargetProperty = Path.DataProperty;
			object obj38 = val69.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			XamlDynamicSetters.<>XamlDynamicSetter_2(obj35, obj38);
			context.PopParent();
			((ISupportInitialize)obj34).EndInit();
			context.PopParent();
			((ISupportInitialize)obj32).EndInit();
			Controls children6 = ((Panel)obj17).Children;
			TextBlock val70 = new TextBlock();
			TextBlock val71 = val70;
			((ISupportInitialize)val70).BeginInit();
			((AvaloniaList<Control>)(object)children6).Add((Control)val70);
			TextBlock val72;
			TextBlock obj39 = (val72 = val71);
			context.PushParent(val72);
			TextBlock obj40 = val72;
			((StyledElement)obj40).Name = "CurrentTimeText";
			obj = obj40;
			context.AvaloniaNameScope.Register("CurrentTimeText", obj);
			Grid.SetColumn((Control)(object)obj40, 3);
			obj40.Text = "00:00";
			((Layoutable)obj40).VerticalAlignment = (VerticalAlignment)2;
			((Layoutable)obj40).Margin = new Thickness(0.0, 0.0, 4.0, 0.0);
			StyledProperty<double> fontSizeProperty = TextBlock.FontSizeProperty;
			ReflectionBindingExtension val73 = new ReflectionBindingExtension("ControlFontSize")
			{
				ElementName = "Root"
			};
			context.ProvideTargetProperty = TextBlock.FontSizeProperty;
			Binding obj41 = val73.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)obj40, (AvaloniaProperty)(object)fontSizeProperty, (IBinding)(object)obj41, (object)null);
			context.PopParent();
			((ISupportInitialize)obj39).EndInit();
			Controls children7 = ((Panel)obj17).Children;
			Slider val74 = new Slider();
			Slider val75 = val74;
			((ISupportInitialize)val74).BeginInit();
			((AvaloniaList<Control>)(object)children7).Add((Control)val74);
			((StyledElement)val75).Name = "SeekBar";
			obj = val75;
			context.AvaloniaNameScope.Register("SeekBar", obj);
			Grid.SetColumn((Control)(object)val75, 4);
			((RangeBase)val75).Minimum = 0.0;
			((RangeBase)val75).Maximum = 100.0;
			((RangeBase)val75).Value = 0.0;
			((Layoutable)val75).MinWidth = 20.0;
			((Layoutable)val75).VerticalAlignment = (VerticalAlignment)2;
			((Layoutable)val75).Margin = new Thickness(0.0, 0.0, 4.0, 0.0);
			((ISupportInitialize)val75).EndInit();
			Controls children8 = ((Panel)obj17).Children;
			TextBlock val76 = new TextBlock();
			TextBlock val77 = val76;
			((ISupportInitialize)val76).BeginInit();
			((AvaloniaList<Control>)(object)children8).Add((Control)val76);
			TextBlock obj42 = (val72 = val77);
			context.PushParent(val72);
			TextBlock obj43 = val72;
			((StyledElement)obj43).Name = "TotalTimeText";
			obj = obj43;
			context.AvaloniaNameScope.Register("TotalTimeText", obj);
			Grid.SetColumn((Control)(object)obj43, 5);
			obj43.Text = "00:00";
			((Layoutable)obj43).VerticalAlignment = (VerticalAlignment)2;
			((Layoutable)obj43).Margin = new Thickness(0.0, 0.0, 4.0, 0.0);
			StyledProperty<double> fontSizeProperty2 = TextBlock.FontSizeProperty;
			ReflectionBindingExtension val78 = new ReflectionBindingExtension("ControlFontSize")
			{
				ElementName = "Root"
			};
			context.ProvideTargetProperty = TextBlock.FontSizeProperty;
			Binding obj44 = val78.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)obj43, (AvaloniaProperty)(object)fontSizeProperty2, (IBinding)(object)obj44, (object)null);
			context.PopParent();
			((ISupportInitialize)obj42).EndInit();
			Controls children9 = ((Panel)obj17).Children;
			Button val79 = new Button();
			Button val80 = val79;
			((ISupportInitialize)val79).BeginInit();
			((AvaloniaList<Control>)(object)children9).Add((Control)val79);
			Button obj45 = (val49 = val80);
			context.PushParent(val49);
			Button obj46 = val49;
			((StyledElement)obj46).Name = "VolumeButton";
			obj = obj46;
			context.AvaloniaNameScope.Register("VolumeButton", obj);
			Grid.SetColumn((Control)(object)obj46, 6);
			((Layoutable)obj46).Margin = new Thickness(0.0, 0.0, 0.0, 0.0);
			((StyledElement)obj46).Classes.Add("iconButton");
			((Interactive)obj46).AddHandler((RoutedEvent)(object)val49.ClickEvent, (Delegate)new EventHandler<RoutedEventArgs>(context.RootObject.OnMuteClick), (RoutingStrategies)5, false);
			Path val81 = new Path();
			Path val82 = val81;
			((ISupportInitialize)val81).BeginInit();
			((ContentControl)obj46).Content = (object)val81;
			Path obj47 = (val52 = val82);
			context.PushParent(val52);
			Path obj48 = val52;
			((StyledElement)obj48).Name = "VolumeIcon";
			obj = obj48;
			context.AvaloniaNameScope.Register("VolumeIcon", obj);
			StyledProperty<double> widthProperty4 = Layoutable.WidthProperty;
			ReflectionBindingExtension val83 = new ReflectionBindingExtension("IconSize")
			{
				ElementName = "Root"
			};
			context.ProvideTargetProperty = Layoutable.WidthProperty;
			Binding obj49 = val83.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)obj48, (AvaloniaProperty)(object)widthProperty4, (IBinding)(object)obj49, (object)null);
			StyledProperty<double> heightProperty4 = Layoutable.HeightProperty;
			ReflectionBindingExtension val84 = new ReflectionBindingExtension("IconSize")
			{
				ElementName = "Root"
			};
			context.ProvideTargetProperty = Layoutable.HeightProperty;
			Binding obj50 = val84.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)obj48, (AvaloniaProperty)(object)heightProperty4, (IBinding)(object)obj50, (object)null);
			((Shape)obj48).Stretch = (Stretch)2;
			((Shape)obj48).Fill = (IBrush)new ImmutableSolidColorBrush(4281545523u);
			StaticResourceExtension val85 = new StaticResourceExtension((object)"VolumeHighIcon");
			context.ProvideTargetProperty = Path.DataProperty;
			object obj51 = val85.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			XamlDynamicSetters.<>XamlDynamicSetter_2(obj48, obj51);
			context.PopParent();
			((ISupportInitialize)obj47).EndInit();
			context.PopParent();
			((ISupportInitialize)obj45).EndInit();
			Controls children10 = ((Panel)obj17).Children;
			Slider val86 = new Slider();
			Slider val87 = val86;
			((ISupportInitialize)val86).BeginInit();
			((AvaloniaList<Control>)(object)children10).Add((Control)val86);
			((StyledElement)val87).Name = "VolumeSlider";
			obj = val87;
			context.AvaloniaNameScope.Register("VolumeSlider", obj);
			Grid.SetColumn((Control)(object)val87, 7);
			((Layoutable)val87).Width = 50.0;
			((Layoutable)val87).MinWidth = 0.0;
			((RangeBase)val87).Minimum = 0.0;
			((RangeBase)val87).Maximum = 100.0;
			((RangeBase)val87).Value = 100.0;
			((Layoutable)val87).VerticalAlignment = (VerticalAlignment)2;
			((ISupportInitialize)val87).EndInit();
			Controls children11 = ((Panel)obj17).Children;
			Button val88 = new Button();
			Button val89 = val88;
			((ISupportInitialize)val88).BeginInit();
			((AvaloniaList<Control>)(object)children11).Add((Control)val88);
			Button obj52 = (val49 = val89);
			context.PushParent(val49);
			Button obj53 = val49;
			((StyledElement)obj53).Name = "FullscreenButton";
			obj = obj53;
			context.AvaloniaNameScope.Register("FullscreenButton", obj);
			Grid.SetColumn((Control)(object)obj53, 8);
			((Layoutable)obj53).Margin = new Thickness(2.0, 0.0, 0.0, 0.0);
			((StyledElement)obj53).Classes.Add("iconButton");
			((Interactive)obj53).AddHandler((RoutedEvent)(object)val49.ClickEvent, (Delegate)new EventHandler<RoutedEventArgs>(context.RootObject.OnFullscreenClick), (RoutingStrategies)5, false);
			Path val90 = new Path();
			Path val91 = val90;
			((ISupportInitialize)val90).BeginInit();
			((ContentControl)obj53).Content = (object)val90;
			Path obj54 = (val52 = val91);
			context.PushParent(val52);
			Path obj55 = val52;
			((StyledElement)obj55).Name = "FullscreenIcon";
			obj = obj55;
			context.AvaloniaNameScope.Register("FullscreenIcon", obj);
			StyledProperty<double> widthProperty5 = Layoutable.WidthProperty;
			ReflectionBindingExtension val92 = new ReflectionBindingExtension("IconSize")
			{
				ElementName = "Root"
			};
			context.ProvideTargetProperty = Layoutable.WidthProperty;
			Binding obj56 = val92.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)obj55, (AvaloniaProperty)(object)widthProperty5, (IBinding)(object)obj56, (object)null);
			StyledProperty<double> heightProperty5 = Layoutable.HeightProperty;
			ReflectionBindingExtension val93 = new ReflectionBindingExtension("IconSize")
			{
				ElementName = "Root"
			};
			context.ProvideTargetProperty = Layoutable.HeightProperty;
			Binding obj57 = val93.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)obj55, (AvaloniaProperty)(object)heightProperty5, (IBinding)(object)obj57, (object)null);
			((Shape)obj55).Stretch = (Stretch)2;
			((Shape)obj55).Fill = (IBrush)new ImmutableSolidColorBrush(4281545523u);
			StaticResourceExtension val94 = new StaticResourceExtension((object)"FullscreenIcon");
			context.ProvideTargetProperty = Path.DataProperty;
			object obj58 = val94.ProvideValue((IServiceProvider)context);
			context.ProvideTargetProperty = null;
			XamlDynamicSetters.<>XamlDynamicSetter_2(obj55, obj58);
			context.PopParent();
			((ISupportInitialize)obj54).EndInit();
			context.PopParent();
			((ISupportInitialize)obj52).EndInit();
			context.PopParent();
			((ISupportInitialize)obj16).EndInit();
			context.PopParent();
			((ISupportInitialize)obj13).EndInit();
			context.PopParent();
			((ISupportInitialize)obj8).EndInit();
			context.PopParent();
			((ISupportInitialize)P_1).EndInit();
			StyledElement val95;
			if ((val95 = (StyledElement)(object)((P_1 is StyledElement) ? P_1 : null)) != null)
			{
				NameScope.SetNameScope(val95, context.AvaloniaNameScope);
			}
			context.AvaloniaNameScope.Complete();
		}

		[CompilerGenerated]
		private static void !XamlIlPopulateTrampoline(VideoPlayerControl? P_0)
		{
			if (!XamlIlPopulateOverride != null)
			{
				!XamlIlPopulateOverride(P_0);
			}
			else
			{
				!XamlIlPopulate(XamlIlRuntimeHelpers.CreateRootServiceProviderV3((IServiceProvider)null), P_0);
			}
		}
	}
	/// <summary>
	/// Provides data for the MediaOpened event.
	/// </summary>
	public sealed class MediaOpenedEventArgs : EventArgs
	{
		/// <summary>
		/// Gets the full path of the media that was opened.
		/// </summary>
		public string Path { get; }

		public MediaOpenedEventArgs(string path)
		{
			Path = path;
		}
	}
	/// <summary>
	/// Interface for providing custom icon geometries.
	/// Implement this interface to provide custom icons for the video player control.
	/// </summary>
	public interface IIconProvider
	{
		/// <summary>
		/// Creates a play icon geometry (triangle pointing right).
		/// </summary>
		Geometry CreatePlayIcon();

		/// <summary>
		/// Creates a pause icon geometry (two vertical bars).
		/// </summary>
		Geometry CreatePauseIcon();

		/// <summary>
		/// Creates a stop icon geometry (square).
		/// </summary>
		Geometry CreateStopIcon();

		/// <summary>
		/// Creates a folder open icon geometry.
		/// </summary>
		Geometry CreateFolderOpenIcon();

		/// <summary>
		/// Creates a volume high icon geometry (speaker with sound waves).
		/// </summary>
		Geometry CreateVolumeHighIcon();

		/// <summary>
		/// Creates a volume off icon geometry (speaker with X).
		/// </summary>
		Geometry CreateVolumeOffIcon();

		/// <summary>
		/// Creates a fullscreen icon geometry (expand arrows).
		/// </summary>
		Geometry CreateFullscreenIcon()
		{
			return Geometry.Parse("M 5,5 L 5,10 L 7,10 L 7,7 L 10,7 L 10,5 Z M 14,5 L 14,7 L 17,7 L 17,10 L 19,10 L 19,5 Z M 7,14 L 5,14 L 5,19 L 10,19 L 10,17 L 7,17 Z M 17,17 L 14,17 L 14,19 L 19,19 L 19,14 L 17,14 Z");
		}

		/// <summary>
		/// Creates a fullscreen exit icon geometry (collapse arrows).
		/// </summary>
		Geometry CreateFullscreenExitIcon()
		{
			return Geometry.Parse("M 14,14 L 14,19 L 16,19 L 16,16 L 19,16 L 19,14 Z M 5,14 L 5,16 L 8,16 L 8,19 L 10,19 L 10,14 Z M 16,5 L 14,5 L 14,10 L 19,10 L 19,8 L 16,8 Z M 8,8 L 5,8 L 5,10 L 10,10 L 10,5 L 8,5 Z");
		}
	}
	/// <summary>
	/// Default icon provider using standard Avalonia shapes.
	/// </summary>
	public sealed class DefaultIconProvider : IIconProvider
	{
		/// <summary>
		/// Gets the singleton instance of the default icon provider.
		/// </summary>
		public static DefaultIconProvider Instance { get; } = new DefaultIconProvider();

		private DefaultIconProvider()
		{
		}

		/// <summary>
		/// Creates a play icon geometry (triangle pointing right).
		/// </summary>
		public Geometry CreatePlayIcon()
		{
			//IL_0000: Unknown result type (might be due to invalid IL or missing references)
			//IL_0005: Unknown result type (might be due to invalid IL or missing references)
			//IL_000a: Unknown result type (might be due to invalid IL or missing references)
			//IL_001d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0028: Expected O, but got Unknown
			//IL_002e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0033: Unknown result type (might be due to invalid IL or missing references)
			//IL_0046: Unknown result type (might be due to invalid IL or missing references)
			//IL_0055: Expected O, but got Unknown
			//IL_005b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0060: Unknown result type (might be due to invalid IL or missing references)
			//IL_0073: Unknown result type (might be due to invalid IL or missing references)
			//IL_0082: Expected O, but got Unknown
			//IL_0088: Unknown result type (might be due to invalid IL or missing references)
			//IL_008d: Unknown result type (might be due to invalid IL or missing references)
			//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
			//IL_00af: Expected O, but got Unknown
			//IL_00af: Unknown result type (might be due to invalid IL or missing references)
			//IL_00bc: Expected O, but got Unknown
			PathGeometry val = new PathGeometry();
			PathFigure val2 = new PathFigure
			{
				StartPoint = new Point(6.0, 4.0)
			};
			((AvaloniaList<PathSegment>)(object)val2.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(6.0, 16.0)
			});
			((AvaloniaList<PathSegment>)(object)val2.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(18.0, 10.0)
			});
			((AvaloniaList<PathSegment>)(object)val2.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(6.0, 4.0)
			});
			((AvaloniaList<PathFigure>)(object)val.Figures).Add(val2);
			return (Geometry)val;
		}

		/// <summary>
		/// Creates a pause icon geometry (two vertical bars).
		/// </summary>
		public Geometry CreatePauseIcon()
		{
			//IL_0000: Unknown result type (might be due to invalid IL or missing references)
			//IL_0005: Unknown result type (might be due to invalid IL or missing references)
			//IL_000a: Unknown result type (might be due to invalid IL or missing references)
			//IL_001d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0028: Expected O, but got Unknown
			//IL_002e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0033: Unknown result type (might be due to invalid IL or missing references)
			//IL_0046: Unknown result type (might be due to invalid IL or missing references)
			//IL_0055: Expected O, but got Unknown
			//IL_005b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0060: Unknown result type (might be due to invalid IL or missing references)
			//IL_0073: Unknown result type (might be due to invalid IL or missing references)
			//IL_0082: Expected O, but got Unknown
			//IL_0088: Unknown result type (might be due to invalid IL or missing references)
			//IL_008d: Unknown result type (might be due to invalid IL or missing references)
			//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
			//IL_00af: Expected O, but got Unknown
			//IL_00b6: Unknown result type (might be due to invalid IL or missing references)
			//IL_00c2: Unknown result type (might be due to invalid IL or missing references)
			//IL_00c7: Unknown result type (might be due to invalid IL or missing references)
			//IL_00da: Unknown result type (might be due to invalid IL or missing references)
			//IL_00e5: Expected O, but got Unknown
			//IL_00eb: Unknown result type (might be due to invalid IL or missing references)
			//IL_00f0: Unknown result type (might be due to invalid IL or missing references)
			//IL_0103: Unknown result type (might be due to invalid IL or missing references)
			//IL_0112: Expected O, but got Unknown
			//IL_0118: Unknown result type (might be due to invalid IL or missing references)
			//IL_011d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0130: Unknown result type (might be due to invalid IL or missing references)
			//IL_013f: Expected O, but got Unknown
			//IL_0145: Unknown result type (might be due to invalid IL or missing references)
			//IL_014a: Unknown result type (might be due to invalid IL or missing references)
			//IL_015d: Unknown result type (might be due to invalid IL or missing references)
			//IL_016c: Expected O, but got Unknown
			//IL_0173: Unknown result type (might be due to invalid IL or missing references)
			//IL_0180: Expected O, but got Unknown
			PathGeometry val = new PathGeometry();
			PathFigure val2 = new PathFigure
			{
				StartPoint = new Point(6.0, 4.0)
			};
			((AvaloniaList<PathSegment>)(object)val2.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(10.0, 4.0)
			});
			((AvaloniaList<PathSegment>)(object)val2.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(10.0, 16.0)
			});
			((AvaloniaList<PathSegment>)(object)val2.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(6.0, 16.0)
			});
			val2.IsClosed = true;
			((AvaloniaList<PathFigure>)(object)val.Figures).Add(val2);
			PathFigure val3 = new PathFigure
			{
				StartPoint = new Point(14.0, 4.0)
			};
			((AvaloniaList<PathSegment>)(object)val3.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(18.0, 4.0)
			});
			((AvaloniaList<PathSegment>)(object)val3.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(18.0, 16.0)
			});
			((AvaloniaList<PathSegment>)(object)val3.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(14.0, 16.0)
			});
			val3.IsClosed = true;
			((AvaloniaList<PathFigure>)(object)val.Figures).Add(val3);
			return (Geometry)val;
		}

		/// <summary>
		/// Creates a stop icon geometry (square).
		/// </summary>
		public Geometry CreateStopIcon()
		{
			//IL_0000: Unknown result type (might be due to invalid IL or missing references)
			//IL_0005: Unknown result type (might be due to invalid IL or missing references)
			//IL_002a: Unknown result type (might be due to invalid IL or missing references)
			//IL_0035: Expected O, but got Unknown
			return (Geometry)new RectangleGeometry
			{
				Rect = new Rect(6.0, 6.0, 12.0, 12.0)
			};
		}

		/// <summary>
		/// Creates a folder open icon geometry.
		/// </summary>
		public Geometry CreateFolderOpenIcon()
		{
			//IL_0000: Unknown result type (might be due to invalid IL or missing references)
			//IL_0005: Unknown result type (might be due to invalid IL or missing references)
			//IL_000a: Unknown result type (might be due to invalid IL or missing references)
			//IL_001d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0028: Expected O, but got Unknown
			//IL_002e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0033: Unknown result type (might be due to invalid IL or missing references)
			//IL_0046: Unknown result type (might be due to invalid IL or missing references)
			//IL_0055: Expected O, but got Unknown
			//IL_005b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0060: Unknown result type (might be due to invalid IL or missing references)
			//IL_0073: Unknown result type (might be due to invalid IL or missing references)
			//IL_0082: Expected O, but got Unknown
			//IL_0088: Unknown result type (might be due to invalid IL or missing references)
			//IL_008d: Unknown result type (might be due to invalid IL or missing references)
			//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
			//IL_00af: Expected O, but got Unknown
			//IL_00b5: Unknown result type (might be due to invalid IL or missing references)
			//IL_00ba: Unknown result type (might be due to invalid IL or missing references)
			//IL_00cd: Unknown result type (might be due to invalid IL or missing references)
			//IL_00dc: Expected O, but got Unknown
			//IL_00e2: Unknown result type (might be due to invalid IL or missing references)
			//IL_00e7: Unknown result type (might be due to invalid IL or missing references)
			//IL_00fa: Unknown result type (might be due to invalid IL or missing references)
			//IL_0109: Expected O, but got Unknown
			//IL_010f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0114: Unknown result type (might be due to invalid IL or missing references)
			//IL_0127: Unknown result type (might be due to invalid IL or missing references)
			//IL_0136: Expected O, but got Unknown
			//IL_013c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0141: Unknown result type (might be due to invalid IL or missing references)
			//IL_0154: Unknown result type (might be due to invalid IL or missing references)
			//IL_0163: Expected O, but got Unknown
			//IL_0169: Unknown result type (might be due to invalid IL or missing references)
			//IL_016e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0181: Unknown result type (might be due to invalid IL or missing references)
			//IL_0190: Expected O, but got Unknown
			//IL_0190: Unknown result type (might be due to invalid IL or missing references)
			//IL_019c: Unknown result type (might be due to invalid IL or missing references)
			//IL_01a1: Unknown result type (might be due to invalid IL or missing references)
			//IL_01b4: Unknown result type (might be due to invalid IL or missing references)
			//IL_01bf: Expected O, but got Unknown
			//IL_01c5: Unknown result type (might be due to invalid IL or missing references)
			//IL_01ca: Unknown result type (might be due to invalid IL or missing references)
			//IL_01dd: Unknown result type (might be due to invalid IL or missing references)
			//IL_01ec: Expected O, but got Unknown
			//IL_01f2: Unknown result type (might be due to invalid IL or missing references)
			//IL_01f7: Unknown result type (might be due to invalid IL or missing references)
			//IL_020a: Unknown result type (might be due to invalid IL or missing references)
			//IL_0219: Expected O, but got Unknown
			//IL_021f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0224: Unknown result type (might be due to invalid IL or missing references)
			//IL_0237: Unknown result type (might be due to invalid IL or missing references)
			//IL_0246: Expected O, but got Unknown
			//IL_0246: Unknown result type (might be due to invalid IL or missing references)
			//IL_0253: Expected O, but got Unknown
			PathGeometry val = new PathGeometry();
			PathFigure val2 = new PathFigure
			{
				StartPoint = new Point(4.0, 6.0)
			};
			((AvaloniaList<PathSegment>)(object)val2.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(4.0, 8.0)
			});
			((AvaloniaList<PathSegment>)(object)val2.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(6.0, 8.0)
			});
			((AvaloniaList<PathSegment>)(object)val2.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(8.0, 6.0)
			});
			((AvaloniaList<PathSegment>)(object)val2.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(16.0, 6.0)
			});
			((AvaloniaList<PathSegment>)(object)val2.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(18.0, 8.0)
			});
			((AvaloniaList<PathSegment>)(object)val2.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(18.0, 16.0)
			});
			((AvaloniaList<PathSegment>)(object)val2.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(4.0, 16.0)
			});
			((AvaloniaList<PathSegment>)(object)val2.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(4.0, 6.0)
			});
			((AvaloniaList<PathFigure>)(object)val.Figures).Add(val2);
			PathFigure val3 = new PathFigure
			{
				StartPoint = new Point(4.0, 8.0)
			};
			((AvaloniaList<PathSegment>)(object)val3.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(6.0, 10.0)
			});
			((AvaloniaList<PathSegment>)(object)val3.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(18.0, 10.0)
			});
			((AvaloniaList<PathSegment>)(object)val3.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(18.0, 8.0)
			});
			((AvaloniaList<PathFigure>)(object)val.Figures).Add(val3);
			return (Geometry)val;
		}

		/// <summary>
		/// Creates a volume high icon geometry (speaker with sound waves).
		/// </summary>
		public Geometry CreateVolumeHighIcon()
		{
			//IL_0000: Unknown result type (might be due to invalid IL or missing references)
			//IL_0005: Unknown result type (might be due to invalid IL or missing references)
			//IL_000a: Unknown result type (might be due to invalid IL or missing references)
			//IL_001d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0028: Expected O, but got Unknown
			//IL_002e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0033: Unknown result type (might be due to invalid IL or missing references)
			//IL_0046: Unknown result type (might be due to invalid IL or missing references)
			//IL_0055: Expected O, but got Unknown
			//IL_005b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0060: Unknown result type (might be due to invalid IL or missing references)
			//IL_0073: Unknown result type (might be due to invalid IL or missing references)
			//IL_0082: Expected O, but got Unknown
			//IL_0088: Unknown result type (might be due to invalid IL or missing references)
			//IL_008d: Unknown result type (might be due to invalid IL or missing references)
			//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
			//IL_00af: Expected O, but got Unknown
			//IL_00b5: Unknown result type (might be due to invalid IL or missing references)
			//IL_00ba: Unknown result type (might be due to invalid IL or missing references)
			//IL_00cd: Unknown result type (might be due to invalid IL or missing references)
			//IL_00dc: Expected O, but got Unknown
			//IL_00e2: Unknown result type (might be due to invalid IL or missing references)
			//IL_00e7: Unknown result type (might be due to invalid IL or missing references)
			//IL_00fa: Unknown result type (might be due to invalid IL or missing references)
			//IL_0109: Expected O, but got Unknown
			//IL_010f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0114: Unknown result type (might be due to invalid IL or missing references)
			//IL_0127: Unknown result type (might be due to invalid IL or missing references)
			//IL_0136: Expected O, but got Unknown
			//IL_0136: Unknown result type (might be due to invalid IL or missing references)
			//IL_0142: Unknown result type (might be due to invalid IL or missing references)
			//IL_0147: Unknown result type (might be due to invalid IL or missing references)
			//IL_015a: Unknown result type (might be due to invalid IL or missing references)
			//IL_0165: Expected O, but got Unknown
			//IL_016b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0170: Unknown result type (might be due to invalid IL or missing references)
			//IL_0183: Unknown result type (might be due to invalid IL or missing references)
			//IL_018d: Unknown result type (might be due to invalid IL or missing references)
			//IL_01a0: Unknown result type (might be due to invalid IL or missing references)
			//IL_01aa: Unknown result type (might be due to invalid IL or missing references)
			//IL_01b6: Expected O, but got Unknown
			//IL_01b6: Unknown result type (might be due to invalid IL or missing references)
			//IL_01c2: Unknown result type (might be due to invalid IL or missing references)
			//IL_01c7: Unknown result type (might be due to invalid IL or missing references)
			//IL_01da: Unknown result type (might be due to invalid IL or missing references)
			//IL_01e5: Expected O, but got Unknown
			//IL_01eb: Unknown result type (might be due to invalid IL or missing references)
			//IL_01f0: Unknown result type (might be due to invalid IL or missing references)
			//IL_0203: Unknown result type (might be due to invalid IL or missing references)
			//IL_020d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0220: Unknown result type (might be due to invalid IL or missing references)
			//IL_022a: Unknown result type (might be due to invalid IL or missing references)
			//IL_0236: Expected O, but got Unknown
			//IL_0236: Unknown result type (might be due to invalid IL or missing references)
			//IL_0243: Expected O, but got Unknown
			PathGeometry val = new PathGeometry();
			PathFigure val2 = new PathFigure
			{
				StartPoint = new Point(5.0, 6.0)
			};
			((AvaloniaList<PathSegment>)(object)val2.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(5.0, 14.0)
			});
			((AvaloniaList<PathSegment>)(object)val2.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(9.0, 14.0)
			});
			((AvaloniaList<PathSegment>)(object)val2.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(13.0, 18.0)
			});
			((AvaloniaList<PathSegment>)(object)val2.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(13.0, 2.0)
			});
			((AvaloniaList<PathSegment>)(object)val2.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(9.0, 6.0)
			});
			((AvaloniaList<PathSegment>)(object)val2.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(5.0, 6.0)
			});
			((AvaloniaList<PathFigure>)(object)val.Figures).Add(val2);
			PathFigure val3 = new PathFigure
			{
				StartPoint = new Point(15.0, 8.0)
			};
			((AvaloniaList<PathSegment>)(object)val3.Segments).Add((PathSegment)new ArcSegment
			{
				Point = new Point(15.0, 12.0),
				Size = new Size(2.0, 2.0),
				SweepDirection = (SweepDirection)1
			});
			((AvaloniaList<PathFigure>)(object)val.Figures).Add(val3);
			PathFigure val4 = new PathFigure
			{
				StartPoint = new Point(17.0, 6.0)
			};
			((AvaloniaList<PathSegment>)(object)val4.Segments).Add((PathSegment)new ArcSegment
			{
				Point = new Point(17.0, 14.0),
				Size = new Size(3.0, 3.0),
				SweepDirection = (SweepDirection)1
			});
			((AvaloniaList<PathFigure>)(object)val.Figures).Add(val4);
			return (Geometry)val;
		}

		/// <summary>
		/// Creates a volume off icon geometry (speaker with X).
		/// </summary>
		public Geometry CreateVolumeOffIcon()
		{
			//IL_0000: Unknown result type (might be due to invalid IL or missing references)
			//IL_0005: Unknown result type (might be due to invalid IL or missing references)
			//IL_000a: Unknown result type (might be due to invalid IL or missing references)
			//IL_001d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0028: Expected O, but got Unknown
			//IL_002e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0033: Unknown result type (might be due to invalid IL or missing references)
			//IL_0046: Unknown result type (might be due to invalid IL or missing references)
			//IL_0055: Expected O, but got Unknown
			//IL_005b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0060: Unknown result type (might be due to invalid IL or missing references)
			//IL_0073: Unknown result type (might be due to invalid IL or missing references)
			//IL_0082: Expected O, but got Unknown
			//IL_0088: Unknown result type (might be due to invalid IL or missing references)
			//IL_008d: Unknown result type (might be due to invalid IL or missing references)
			//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
			//IL_00af: Expected O, but got Unknown
			//IL_00b5: Unknown result type (might be due to invalid IL or missing references)
			//IL_00ba: Unknown result type (might be due to invalid IL or missing references)
			//IL_00cd: Unknown result type (might be due to invalid IL or missing references)
			//IL_00dc: Expected O, but got Unknown
			//IL_00e2: Unknown result type (might be due to invalid IL or missing references)
			//IL_00e7: Unknown result type (might be due to invalid IL or missing references)
			//IL_00fa: Unknown result type (might be due to invalid IL or missing references)
			//IL_0109: Expected O, but got Unknown
			//IL_010f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0114: Unknown result type (might be due to invalid IL or missing references)
			//IL_0127: Unknown result type (might be due to invalid IL or missing references)
			//IL_0136: Expected O, but got Unknown
			//IL_0136: Unknown result type (might be due to invalid IL or missing references)
			//IL_0142: Unknown result type (might be due to invalid IL or missing references)
			//IL_0147: Unknown result type (might be due to invalid IL or missing references)
			//IL_015a: Unknown result type (might be due to invalid IL or missing references)
			//IL_0165: Expected O, but got Unknown
			//IL_016b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0170: Unknown result type (might be due to invalid IL or missing references)
			//IL_0183: Unknown result type (might be due to invalid IL or missing references)
			//IL_0192: Expected O, but got Unknown
			//IL_0192: Unknown result type (might be due to invalid IL or missing references)
			//IL_019e: Unknown result type (might be due to invalid IL or missing references)
			//IL_01a3: Unknown result type (might be due to invalid IL or missing references)
			//IL_01b6: Unknown result type (might be due to invalid IL or missing references)
			//IL_01c1: Expected O, but got Unknown
			//IL_01c7: Unknown result type (might be due to invalid IL or missing references)
			//IL_01cc: Unknown result type (might be due to invalid IL or missing references)
			//IL_01df: Unknown result type (might be due to invalid IL or missing references)
			//IL_01ee: Expected O, but got Unknown
			//IL_01ee: Unknown result type (might be due to invalid IL or missing references)
			//IL_01fb: Expected O, but got Unknown
			PathGeometry val = new PathGeometry();
			PathFigure val2 = new PathFigure
			{
				StartPoint = new Point(5.0, 6.0)
			};
			((AvaloniaList<PathSegment>)(object)val2.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(5.0, 14.0)
			});
			((AvaloniaList<PathSegment>)(object)val2.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(9.0, 14.0)
			});
			((AvaloniaList<PathSegment>)(object)val2.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(13.0, 18.0)
			});
			((AvaloniaList<PathSegment>)(object)val2.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(13.0, 2.0)
			});
			((AvaloniaList<PathSegment>)(object)val2.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(9.0, 6.0)
			});
			((AvaloniaList<PathSegment>)(object)val2.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(5.0, 6.0)
			});
			((AvaloniaList<PathFigure>)(object)val.Figures).Add(val2);
			PathFigure val3 = new PathFigure
			{
				StartPoint = new Point(15.0, 5.0)
			};
			((AvaloniaList<PathSegment>)(object)val3.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(19.0, 9.0)
			});
			((AvaloniaList<PathFigure>)(object)val.Figures).Add(val3);
			PathFigure val4 = new PathFigure
			{
				StartPoint = new Point(15.0, 9.0)
			};
			((AvaloniaList<PathSegment>)(object)val4.Segments).Add((PathSegment)new LineSegment
			{
				Point = new Point(19.0, 5.0)
			});
			((AvaloniaList<PathFigure>)(object)val.Figures).Add(val4);
			return (Geometry)val;
		}
	}
}
namespace CompiledAvaloniaXaml
{
	[CompilerGenerated]
	internal class XamlIlHelpers
	{
		private static IPropertyInfo Avalonia.Styling.Setter,Avalonia.Base.Value!Field;

		private static object Avalonia.Styling.Setter,Avalonia.Base.Value!Getter(object P_0)
		{
			//IL_0001: Unknown result type (might be due to invalid IL or missing references)
			return ((Setter)P_0).Value;
		}

		private static void Avalonia.Styling.Setter,Avalonia.Base.Value!Setter(object P_0, object P_1)
		{
			//IL_0001: Unknown result type (might be due to invalid IL or missing references)
			((Setter)P_0).Value = P_1;
		}

		public static IPropertyInfo Avalonia.Styling.Setter,Avalonia.Base.Value!Property()
		{
			//IL_0037: Unknown result type (might be due to invalid IL or missing references)
			//IL_0041: Expected O, but got Unknown
			if (Avalonia.Styling.Setter,Avalonia.Base.Value!Field != null)
			{
				return Avalonia.Styling.Setter,Avalonia.Base.Value!Field;
			}
			Avalonia.Styling.Setter,Avalonia.Base.Value!Field = (IPropertyInfo)new ClrPropertyInfo("Value", (Func<object, object>)Avalonia.Styling.Setter,Avalonia.Base.Value!Getter, (Action<object, object>)Avalonia.Styling.Setter,Avalonia.Base.Value!Setter, typeof(object));
			return Avalonia.Styling.Setter,Avalonia.Base.Value!Field;
		}
	}
	[CompilerGenerated]
	internal class !IndexerAccessorFactoryClosure
	{
	}
	[CompilerGenerated]
	internal class XamlIlTrampolines
	{
	}
	[CompilerGenerated]
	internal class XamlDynamicSetters
	{
		public static void <>XamlDynamicSetter_1(ContentPresenter P_0, BindingPriority P_1, IBinding P_2)
		{
			//IL_0001: Unknown result type (might be due to invalid IL or missing references)
			//IL_001b: Expected I4, but got Unknown
			if (P_2 != null)
			{
				IBinding val = P_2;
				AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)P_0, (AvaloniaProperty)(object)ContentPresenter.ContentProperty, val, (object)null);
			}
			else
			{
				object obj = P_2;
				int num = (int)P_1;
				((AvaloniaObject)P_0).SetValue<object>(ContentPresenter.ContentProperty, obj, (BindingPriority)num);
			}
		}

		public static void <>XamlDynamicSetter_2(Path P_0, object P_1)
		{
			if (P_1 is UnsetValueType)
			{
				((AvaloniaObject)P_0).SetValue((AvaloniaProperty)(object)Path.DataProperty, AvaloniaProperty.UnsetValue, (BindingPriority)0);
				return;
			}
			if (P_1 is IBinding)
			{
				IBinding val = (IBinding)P_1;
				AvaloniaObjectExtensions.Bind((AvaloniaObject)(object)P_0, (AvaloniaProperty)(object)Path.DataProperty, val, (object)null);
				return;
			}
			if (P_1 is Geometry)
			{
				P_0.Data = (Geometry)P_1;
				return;
			}
			if (P_1 == null)
			{
				P_0.Data = (Geometry)P_1;
				return;
			}
			throw new InvalidCastException();
		}
	}
	[CompilerGenerated]
	internal class XamlIlContext
	{
		[CompilerGenerated]
		public class Context<TTarget> : IRootObjectProvider, IAvaloniaXamlIlParentStackProvider, ITypeDescriptorContext, IProvideValueTarget, IUriContext, IServiceProvider, IAvaloniaXamlIlEagerParentStackProvider
		{
			public TTarget RootObject;

			public object IntermediateRoot;

			private IServiceProvider _sp;

			private IServiceProvider _innerSp;

			private object[] _staticProviders;

			public List<object> ParentsStack;

			private IEnumerable<object> _parentStackEnumerable;

			public object ProvideTargetObject;

			public object ProvideTargetProperty;

			private Uri _baseUri;

			public INameScope AvaloniaNameScope;

			virtual object IRootObjectProvider.RootObject
			{
				get
				{
					//IL_003c: Unknown result type (might be due to invalid IL or missing references)
					//IL_0042: Expected O, but got Unknown
					if (RootObject != null)
					{
						return RootObject;
					}
					if (_sp != null)
					{
						IRootObjectProvider val = (IRootObjectProvider)_sp.GetService(typeof(IRootObjectProvider));
						if (val != null)
						{
							return val.RootObject;
						}
					}
					return null;
				}
			}

			virtual object IRootObjectProvider.IntermediateRootObject => IntermediateRoot;

			virtual IEnumerable<object> IAvaloniaXamlIlParentStackProvider.Parents => _parentStackEnumerable;

			virtual IContainer ITypeDescriptorContext.Container => null;

			virtual object ITypeDescriptorContext.Instance => null;

			virtual PropertyDescriptor ITypeDescriptorContext.PropertyDescriptor => null;

			virtual object IProvideValueTarget.TargetObject => ProvideTargetObject;

			virtual object IProvideValueTarget.TargetProperty => ProvideTargetProperty;

			public virtual Uri BaseUri
			{
				get
				{
					return _baseUri;
				}
				set
				{
					_baseUri = baseUri;
				}
			}

			private virtual IReadOnlyList<object> DirectParentsStack => ParentsStack;

			private virtual IAvaloniaXamlIlEagerParentStackProvider ParentProvider => XamlIlRuntimeHelpers.AsEagerParentStackProvider((IAvaloniaXamlIlParentStackProvider)_sp.GetService(typeof(IAvaloniaXamlIlParentStackProvider)));

			virtual bool ITypeDescriptorContext.OnComponentChanging()
			{
				throw new NotSupportedException();
			}

			virtual void ITypeDescriptorContext.OnComponentChanged()
			{
				throw new NotSupportedException();
			}

			public virtual object GetService(Type P_0)
			{
				if (_innerSp != null)
				{
					object service = _innerSp.GetService(P_0);
					if (service != null)
					{
						return service;
					}
				}
				if (typeof(IRootObjectProvider).Equals(P_0))
				{
					return this;
				}
				if (typeof(IAvaloniaXamlIlParentStackProvider).Equals(P_0))
				{
					return this;
				}
				if (typeof(ITypeDescriptorContext).Equals(P_0))
				{
					return this;
				}
				if (typeof(IProvideValueTarget).Equals(P_0))
				{
					return this;
				}
				if (typeof(IUriContext).Equals(P_0))
				{
					return this;
				}
				if (_staticProviders != null)
				{
					for (int i = 0; i < (nint)_staticProviders.LongLength; i++)
					{
						object obj = _staticProviders[i];
						if (P_0.IsAssignableFrom(obj.GetType()))
						{
							return obj;
						}
					}
				}
				if (_sp != null)
				{
					return _sp.GetService(P_0);
				}
				return null;
			}

			public Context(IServiceProvider P_0, object[] P_1, string P_2)
			{
				_sp = P_0;
				_staticProviders = P_1;
				if (P_2 != null)
				{
					_baseUri = new Uri(P_2);
				}
				ParentsStack = new List<object>();
				_parentStackEnumerable = new ParentStackEnumerable(ParentsStack, _sp);
				AvaloniaNameScope = (INameScope)P_0.GetService(typeof(INameScope));
				_innerSp = XamlIlRuntimeHelpers.CreateInnerServiceProviderV1((IServiceProvider)this);
			}

			public void PushParent(object P_0)
			{
				ParentsStack.Add(P_0);
				ProvideTargetObject = P_0;
			}

			public void PopParent()
			{
				int num = ParentsStack.Count - 1;
				ParentsStack.RemoveAt(num);
				ProvideTargetObject = ((num == 0) ? null : ParentsStack[num - 1]);
			}
		}

		[CompilerGenerated]
		private class ParentStackEnumerable : IEnumerable<object>
		{
			[CompilerGenerated]
			public class Enumerator : IEnumerator<object>
			{
				private int _state;

				private List<object> _parentList;

				private IServiceProvider _parentSP;

				private List<object> _list;

				private int _listIndex;

				private object _current;

				private IEnumerator<object> _parentEnumerator;

				public virtual object Current => _current;

				public Enumerator(List<object> P_0, IServiceProvider P_1)
				{
					_parentList = P_0;
					_parentSP = P_1;
				}

				virtual void IEnumerator.Reset()
				{
					throw new NotSupportedException();
				}

				virtual void IDisposable.Dispose()
				{
					if (_parentEnumerator != null)
					{
						_parentEnumerator.Dispose();
					}
				}

				virtual bool IEnumerator.MoveNext()
				{
					//IL_00a4: Unknown result type (might be due to invalid IL or missing references)
					//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
					//IL_00ab: Expected O, but got Unknown
					if (_state != 0)
					{
						if (_state != 1)
						{
							if (_state != 2)
							{
								return false;
							}
							goto IL_00c8;
						}
					}
					else
					{
						_list = _parentList;
						_listIndex = _list.Count - 1;
						_state = 1;
					}
					if (_listIndex >= 0)
					{
						_current = _list[_listIndex];
						_listIndex--;
						return true;
					}
					if (_parentSP != null)
					{
						IAvaloniaXamlIlParentStackProvider val = (IAvaloniaXamlIlParentStackProvider)_parentSP.GetService(typeof(IAvaloniaXamlIlParentStackProvider));
						IAvaloniaXamlIlParentStackProvider val2 = val;
						if ((int)val != 0)
						{
							_parentEnumerator = val2.Parents.GetEnumerator();
							_state = 2;
							goto IL_00c8;
						}
					}
					goto IL_00eb;
					IL_00eb:
					_state = 3;
					return false;
					IL_00c8:
					if (_parentEnumerator.MoveNext())
					{
						_current = _parentEnumerator.Current;
						return true;
					}
					goto IL_00eb;
				}
			}

			private List<object> _parentList;

			private IServiceProvider _parentSP;

			public ParentStackEnumerable(List<object> P_0, IServiceProvider P_1)
			{
				_parentList = P_0;
				_parentSP = P_1;
			}

			public virtual IEnumerator<object> GetEnumerator()
			{
				return new Enumerator(_parentList, _parentSP);
			}

			virtual IEnumerator IEnumerable.GetEnumerator()
			{
				return GetEnumerator();
			}
		}
	}
	[EditorBrowsable(EditorBrowsableState.Never)]
	[CompilerGenerated]
	public class !XamlLoader
	{
		public static object TryLoad(IServiceProvider P_0, string P_1)
		{
			if (string.Equals(P_1, "avares://Avalonia.FFmpegVideoPlayer/VideoPlayerControl.axaml", StringComparison.OrdinalIgnoreCase))
			{
				return new VideoPlayerControl();
			}
			return null;
		}

		public static object TryLoad(string P_0)
		{
			return TryLoad(null, P_0);
		}
	}
	[EditorBrowsable(EditorBrowsableState.Never)]
	[CompilerGenerated]
	public class !AvaloniaResources
	{
		[CompilerGenerated]
		internal class NamespaceInfo:/VideoPlayerControl.axaml : IAvaloniaXamlIlXmlNamespaceInfoProvider
		{
			private IReadOnlyDictionary<string, IReadOnlyList<AvaloniaXamlIlXmlNamespaceInfo>> _xmlNamespaces;

			public static IAvaloniaXamlIlXmlNamespaceInfoProvider Singleton;

			public virtual IReadOnlyDictionary<string, IReadOnlyList<AvaloniaXamlIlXmlNamespaceInfo>> XmlNamespaces
			{
				get
				{
					if (_xmlNamespaces == null)
					{
						_xmlNamespaces = CreateNamespaces();
					}
					return _xmlNamespaces;
				}
			}

			private static AvaloniaXamlIlXmlNamespaceInfo CreateNamespaceInfo(string P_0, string P_1)
			{
				//IL_0000: Unknown result type (might be due to invalid IL or missing references)
				//IL_0005: Unknown result type (might be due to invalid IL or missing references)
				//IL_000c: Unknown result type (might be due to invalid IL or missing references)
				//IL_0014: Expected O, but got Unknown
				return new AvaloniaXamlIlXmlNamespaceInfo
				{
					ClrNamespace = P_0,
					ClrAssemblyName = P_1
				};
			}

			private static IReadOnlyDictionary<string, IReadOnlyList<AvaloniaXamlIlXmlNamespaceInfo>> CreateNamespaces()
			{
				return new Dictionary<string, IReadOnlyList<AvaloniaXamlIlXmlNamespaceInfo>>(5)
				{
					{
						"",
						(IReadOnlyList<AvaloniaXamlIlXmlNamespaceInfo>)(object)new AvaloniaXamlIlXmlNamespaceInfo[31]
						{
							CreateNamespaceInfo("Avalonia", "Avalonia.Base"),
							CreateNamespaceInfo("Avalonia.Animation", "Avalonia.Base"),
							CreateNamespaceInfo("Avalonia.Animation.Easings", "Avalonia.Base"),
							CreateNamespaceInfo("Avalonia.Controls", "Avalonia.Base"),
							CreateNamespaceInfo("Avalonia.Data", "Avalonia.Base"),
							CreateNamespaceInfo("Avalonia.Data.Converters", "Avalonia.Base"),
							CreateNamespaceInfo("Avalonia.Input", "Avalonia.Base"),
							CreateNamespaceInfo("Avalonia.Input.GestureRecognizers", "Avalonia.Base"),
							CreateNamespaceInfo("Avalonia.Input.TextInput", "Avalonia.Base"),
							CreateNamespaceInfo("Avalonia.Layout", "Avalonia.Base"),
							CreateNamespaceInfo("Avalonia.LogicalTree", "Avalonia.Base"),
							CreateNamespaceInfo("Avalonia.Media", "Avalonia.Base"),
							CreateNamespaceInfo("Avalonia.Media.Imaging", "Avalonia.Base"),
							CreateNamespaceInfo("Avalonia.Media.Transformation", "Avalonia.Base"),
							CreateNamespaceInfo("Avalonia.Styling", "Avalonia.Base"),
							CreateNamespaceInfo("Avalonia", "Avalonia.Controls"),
							CreateNamespaceInfo("Avalonia.Automation", "Avalonia.Controls"),
							CreateNamespaceInfo("Avalonia.Controls", "Avalonia.Controls"),
							CreateNamespaceInfo("Avalonia.Controls.Embedding", "Avalonia.Controls"),
							CreateNamespaceInfo("Avalonia.Controls.Presenters", "Avalonia.Controls"),
							CreateNamespaceInfo("Avalonia.Controls.Primitives", "Avalonia.Controls"),
							CreateNamespaceInfo("Avalonia.Controls.Shapes", "Avalonia.Controls"),
							CreateNamespaceInfo("Avalonia.Controls.Templates", "Avalonia.Controls"),
							CreateNamespaceInfo("Avalonia.Controls.Notifications", "Avalonia.Controls"),
							CreateNamespaceInfo("Avalonia.Controls.Chrome", "Avalonia.Controls"),
							CreateNamespaceInfo("Avalonia.Controls.Documents", "Avalonia.Controls"),
							CreateNamespaceInfo("Avalonia.Data", "Avalonia.Markup"),
							CreateNamespaceInfo("Avalonia.Markup.Data", "Avalonia.Markup"),
							CreateNamespaceInfo("Avalonia.Markup.Xaml.MarkupExtensions", "Avalonia.Markup.Xaml"),
							CreateNamespaceInfo("Avalonia.Markup.Xaml.Styling", "Avalonia.Markup.Xaml"),
							CreateNamespaceInfo("Avalonia.Markup.Xaml.Templates", "Avalonia.Markup.Xaml")
						}
					},
					{
						"x",
						(IReadOnlyList<AvaloniaXamlIlXmlNamespaceInfo>)(object)new AvaloniaXamlIlXmlNamespaceInfo[0]
					},
					{
						"d",
						(IReadOnlyList<AvaloniaXamlIlXmlNamespaceInfo>)(object)new AvaloniaXamlIlXmlNamespaceInfo[0]
					},
					{
						"mc",
						(IReadOnlyList<AvaloniaXamlIlXmlNamespaceInfo>)(object)new AvaloniaXamlIlXmlNamespaceInfo[0]
					},
					{
						"local",
						(IReadOnlyList<AvaloniaXamlIlXmlNamespaceInfo>)(object)new AvaloniaXamlIlXmlNamespaceInfo[1] { CreateNamespaceInfo("Avalonia.FFmpegVideoPlayer", "Avalonia.FFmpegVideoPlayer") }
					}
				};
			}

			static NamespaceInfo:/VideoPlayerControl.axaml()
			{
				Singleton = (IAvaloniaXamlIlXmlNamespaceInfoProvider)(object)new NamespaceInfo:/VideoPlayerControl.axaml();
			}
		}
	}
}
