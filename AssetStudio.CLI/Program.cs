using System;
using System.Threading;

namespace AssetStudio.CLI
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            using var cts = new CancellationTokenSource();
            void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
            {
                if (!cts.IsCancellationRequested)
                {
                    e.Cancel = true;
                    cts.Cancel();
                }
            }

            Console.CancelKeyPress += OnCancelKeyPress;
            try
            {
                return CliRunner.Run(args, Console.Out, Console.Error, cts.Token);
            }
            finally
            {
                Console.CancelKeyPress -= OnCancelKeyPress;
            }
        }
    }
}
