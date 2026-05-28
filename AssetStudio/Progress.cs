using System;

namespace AssetStudio
{
    public static class Progress
    {
        public static IProgress<int> Default = new Progress<int>();
        private static int preValue;
        private static readonly object progressLock = new object();

        public static void Reset()
        {
            lock (progressLock)
            {
                preValue = 0;
                Default.Report(0);
            }
        }

        public static void Report(int current, int total)
        {
            var value = (int)(current * 100f / total);
            Report(value);
        }

        private static void Report(int value)
        {
            lock (progressLock)
            {
                if (value > preValue)
                {
                    preValue = value;
                    Default.Report(value);
                }
            }
        }
    }
}
