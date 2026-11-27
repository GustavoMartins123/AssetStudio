#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace AssetStudio
{
    internal sealed class ProcessMemoryHandle : IDisposable
    {
        private ProcessMemoryHandle(int processId, IntPtr nativeHandle, FileStream? memoryStream)
        {
            ProcessId = processId;
            NativeHandle = nativeHandle;
            MemoryStream = memoryStream;
        }

        public static ProcessMemoryHandle FromNative(int processId, IntPtr nativeHandle) => new(processId, nativeHandle, null);
        public static ProcessMemoryHandle FromStream(int processId, FileStream memoryStream) => new(processId, IntPtr.Zero, memoryStream);

        public int ProcessId { get; }
        public IntPtr NativeHandle { get; }
        public FileStream? MemoryStream { get; }

        public void Dispose()
        {
            if (NativeHandle != IntPtr.Zero)
            {
                CloseHandle(NativeHandle);
            }

            MemoryStream?.Dispose();
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }

    internal static class ProcessMemoryReader
    {
        private const uint ProcessQueryInformation = 0x0400;
        private const uint ProcessVmRead = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryBasicInformation
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public uint PartitionId;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
            public uint Padding;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, IntPtr size, out IntPtr bytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int VirtualQueryEx(IntPtr process, IntPtr address, out MemoryBasicInformation info, IntPtr length);

        public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

        public static ProcessMemoryHandle? Open(int processId, out int errorCode)
        {
            errorCode = 0;
            if (OperatingSystem.IsWindows())
            {
                IntPtr process = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
                errorCode = process == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
                return process == IntPtr.Zero ? null : ProcessMemoryHandle.FromNative(processId, process);
            }

            if (!OperatingSystem.IsLinux())
            {
                errorCode = 1;
                return null;
            }

            try
            {
                const int PermissionError = 13;
                errorCode = PermissionError;
                var stream = new FileStream($"/proc/{processId}/mem", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
                if (!CanReadMemory(stream, processId))
                {
                    stream.Dispose();
                    return null;
                }

                return ProcessMemoryHandle.FromStream(processId, stream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static bool CanReadMemory(FileStream stream, int processId)
        {
            try
            {
                string? first = new StreamReader($"/proc/{processId}/maps").ReadLine();
                if (first == null || !TryParseRange(first, out long start, out _))
                {
                    return false;
                }

                Span<byte> probe = stackalloc byte[1];
                stream.Seek(start, SeekOrigin.Begin);
                return stream.Read(probe) == 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return false;
            }
        }

        public static List<(long Address, long Size, long RegionEnd)> EnumerateReadableChunks(ProcessMemoryHandle handle, int chunkSize)
        {
            var chunks = new List<(long Address, long Size, long RegionEnd)>();
            if (handle.NativeHandle != IntPtr.Zero)
            {
                long address = 0;
                while (address < 0x7FFFFFFFFFFFL)
                {
                    int result = VirtualQueryEx(handle.NativeHandle, (IntPtr)address, out MemoryBasicInformation info, (IntPtr)Marshal.SizeOf<MemoryBasicInformation>());
                    if (result == 0)
                    {
                        break;
                    }

                    long baseAddress = info.BaseAddress.ToInt64();
                    long size = info.RegionSize.ToInt64();
                    if (size <= 0)
                    {
                        break;
                    }

                    uint protect = info.Protect & 0xFF;
                    bool readable = info.State == 0x1000 && (info.Type == 0x20000 || info.Type == 0x40000)
                        && (info.Protect & 0x100) == 0 && protect != 0x01;
                    if (readable)
                    {
                        AddChunks(chunks, baseAddress, baseAddress + size, chunkSize);
                    }

                    address = baseAddress + size;
                }

                return chunks;
            }

            AddLinuxChunks(handle, chunks, chunkSize);
            return chunks;
        }

        public static bool TryRead(ProcessMemoryHandle handle, long address, byte[] buffer, int length)
        {
            if (length <= 0 || length > buffer.Length)
            {
                return false;
            }

            if (handle.NativeHandle != IntPtr.Zero)
            {
                return ReadProcessMemory(handle.NativeHandle, (IntPtr)address, buffer, (IntPtr)length, out IntPtr read) && read.ToInt64() == length;
            }

            FileStream? stream = handle.MemoryStream;
            if (stream == null)
            {
                return false;
            }

            try
            {
                stream.Seek(address, SeekOrigin.Begin);
                int total = 0;
                while (total < length)
                {
                    int read = stream.Read(buffer, total, length - total);
                    if (read <= 0)
                    {
                        return false;
                    }

                    total += read;
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return false;
            }
        }

        private static void AddChunks(List<(long Address, long Size, long RegionEnd)> chunks, long start, long end, int chunkSize)
        {
            for (long offset = 0; offset < end - start; offset += chunkSize)
            {
                chunks.Add((start + offset, Math.Min(chunkSize, end - start - offset), end));
            }
        }

        private static void AddLinuxChunks(ProcessMemoryHandle handle, List<(long Address, long Size, long RegionEnd)> chunks, int chunkSize)
        {
            try
            {
                using var reader = new StreamReader($"/proc/{handle.ProcessId}/maps");
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (IsWritableAnonymousRegion(line)
                        && TryParseRange(line, out long start, out long end))
                    {
                        AddChunks(chunks, start, end, chunkSize);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        private static bool IsWritableAnonymousRegion(string line)
        {
            int permissions = line.IndexOf(' ');
            if (permissions < 0 || permissions + 4 >= line.Length)
            {
                return false;
            }

            if (line[permissions + 1] != 'r' || line[permissions + 2] != 'w')
            {
                return false;
            }

            int path = permissions + 5;
            for (int field = 0; field < 3 && path >= 0; field++)
            {
                path = line.IndexOf(' ', path);
            }

            if (path < 0)
            {
                return true;
            }

            string name = line.AsSpan(path + 1).TrimStart().ToString();
            return name.Length == 0
                || (!name.StartsWith('[') && !name.StartsWith("/dev/", StringComparison.Ordinal)
                    && !name.StartsWith("/proc/", StringComparison.Ordinal) && !name.StartsWith("/sys/", StringComparison.Ordinal));
        }

        private static bool TryParseRange(string line, out long start, out long end)
        {
            start = 0;
            end = 0;
            int separator = line.IndexOf('-');
            int space = line.IndexOf(' ');
            return separator > 0
                && space > separator
                && long.TryParse(line.AsSpan(0, separator), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out start)
                && long.TryParse(line.AsSpan(separator + 1, space - separator - 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out end)
                && end > start;
        }
    }
}
