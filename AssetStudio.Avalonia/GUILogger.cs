using AssetStudio;
using Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AssetStudio.Avalonia
{
    class GUILogger : ILogger
    {
        public bool ShowErrorMessage;
        private readonly Action<string> action;
        private readonly List<(LoggerEvent Event, string Message)> messages = new List<(LoggerEvent Event, string Message)>();
        private readonly object messagesLock = new object();
        private readonly Window? owner;

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
                    else
                    {
                        action("Error logged. Export errors.txt to inspect details.");
                    }
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
        }
    }
}
