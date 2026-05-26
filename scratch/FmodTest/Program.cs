using System;
using System.Runtime.InteropServices;

class Program
{
    [DllImport(@"F:\AssetStudio\AssetStudio.Avalonia\bin\Release\net10.0\win-x64\publish\x64\fmod.dll", EntryPoint="FMOD_System_Create")]
    public static extern int FMOD_System_Create(out IntPtr system, uint headerversion);

    [DllImport(@"F:\AssetStudio\AssetStudio.Avalonia\bin\Release\net10.0\win-x64\publish\x64\fmod.dll", EntryPoint="FMOD_System_GetVersion")]
    public static extern int FMOD_System_GetVersion(IntPtr system, out uint version);

    static void Main()
    {
        try
        {
            Console.WriteLine("Calling FMOD_System_Create...");
            IntPtr sys;
            int res = FMOD_System_Create(out sys, 0x00020300);
            Console.WriteLine($"Create Result: {res}, Pointer: {sys}");
            if (res == 0 && sys != IntPtr.Zero)
            {
                Console.WriteLine("Calling FMOD_System_GetVersion...");
                uint ver;
                int resVer = FMOD_System_GetVersion(sys, out ver);
                Console.WriteLine($"GetVersion Result: {resVer}, Version: 0x{ver:X}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception: {ex}");
        }
    }
}
