using AssetStudio;
using Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia
{
    class GUILogger : ILogger
    {
        public bool ShowErrorMessage;
        private readonly Action<string> action;
        private readonly List<(LoggerEvent Event, string Message)> messages = new List<(LoggerEvent Event, string Message)>();
        private readonly object messagesLock = new object();
        private readonly Window? owner;

        private int _pendingErrorCount;
        private int _errorStatusPending;

        public GUILogger(Action<string> action, Window? owner = null)
        {
            this.action = action;
            this.owner = owner;
        }

        public void Log(LoggerEvent loggerEvent, string message)
        {
            switch (loggerEvent)
            {
                case LoggerEvent.Error:
                    AddMessage(loggerEvent, message);
                    if (ShowErrorMessage)
                    {
                        MessageBox.Show(owner, message, "Error");
                    }
                    ScheduleErrorStatusUpdate();
                    break;
                case LoggerEvent.Warning:
                    AddMessage(loggerEvent, message);
                    action(message);
                    break;
                default:
                    action(message);
                    break;
            }
        }

        private void ScheduleErrorStatusUpdate()
        {
            Interlocked.Increment(ref _pendingErrorCount);
            if (Interlocked.CompareExchange(ref _errorStatusPending, 1, 0) == 0)
            {
                Task.Delay(200).ContinueWith(_ =>
                {
                    var count = Interlocked.Exchange(ref _pendingErrorCount, 0);
                    Interlocked.Exchange(ref _errorStatusPending, 0);
                    action($"Error logged ({count} new). Export errors.txt to inspect details.");
                }, TaskScheduler.Default);
            }
        }

        private void AddMessage(LoggerEvent loggerEvent, string message)
        {
            lock (messagesLock)
            {
                messages.Add((loggerEvent, message));
            }
        }

        public string[] GetMessages(params LoggerEvent[] loggerEvents)
        {
            lock (messagesLock)
            {
                return messages
                    .Where(x => loggerEvents.Contains(x.Event))
                    .Select(x => x.Message)
                    .ToArray();
            }
        }

        public void ClearErrors()
        {
            lock (messagesLock)
            {
                messages.Clear();
            }
            Interlocked.Exchange(ref _pendingErrorCount, 0);
        }
    }
}
