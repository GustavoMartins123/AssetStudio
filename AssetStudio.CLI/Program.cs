using System;

namespace AssetStudio.CLI
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            return CliRunner.Run(args, Console.Out, Console.Error);
        }
    }
}
