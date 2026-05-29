using System;

namespace AssetStudio
{
    public class MemoryPressureException : InvalidOperationException
    {
        public int MemoryLoadPercent { get; }
        public int LimitPercent { get; }
        public string Operation { get; }

        public MemoryPressureException(string operation, int memoryLoadPercent, int limitPercent)
            : base($"Asset loading stopped while {operation} because memory pressure reached {memoryLoadPercent}% " +
                   $"(limit {limitPercent}%). Load fewer bundles at once or raise ASSETSTUDIO_MEMORY_LIMIT_PERCENT.")
        {
            Operation = operation;
            MemoryLoadPercent = memoryLoadPercent;
            LimitPercent = limitPercent;
        }
    }
}
