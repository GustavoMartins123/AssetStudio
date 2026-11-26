#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AssetStudio
{
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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        public static IntPtr Open(int processId, out int errorCode)
        {
            IntPtr process = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
            errorCode = process == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            return process;
        }

        public static void Close(IntPtr process)
        {
            if (process != IntPtr.Zero)
            {
                CloseHandle(process);
            }
        }

        public static IEnumerable<(long Address, long Size, long RegionEnd)> EnumerateReadableChunks(IntPtr process, int chunkSize)
        {
            var items = new List<(long Address, long Size, long RegionEnd)>();
            long address = 0;
            while (address < 0x7FFFFFFFFFFFL)
            {
                int result = VirtualQueryEx(process, (IntPtr)address, out MemoryBasicInformation info, (IntPtr)Marshal.SizeOf<MemoryBasicInformation>());
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
                    long end = baseAddress + size;
                    for (long offset = 0; offset < size; offset += chunkSize)
                    {
                        items.Add((baseAddress + offset, Math.Min(chunkSize, size - offset), end));
                    }
                }

                address = baseAddress + size;
            }

            return items;
        }

        public static bool TryRead(IntPtr process, long address, byte[] buffer, int length)
        {
            if (length <= 0 || length > buffer.Length)
            {
                return false;
            }

            return ReadProcessMemory(process, (IntPtr)address, buffer, (IntPtr)length, out IntPtr read) && read.ToInt64() == length;
        }
    }
}
