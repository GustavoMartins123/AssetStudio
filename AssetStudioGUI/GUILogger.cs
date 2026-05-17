using AssetStudio;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace AssetStudioGUI
{
    class GUILogger : ILogger
    {
        public bool ShowErrorMessage;
        private readonly Action<string> action;
        private readonly List<(LoggerEvent Event, string Message)> messages = new List<(LoggerEvent Event, string Message)>();
        private readonly object messagesLock = new object();

        public GUILogger(Action<string> action)
        {
            this.action = action;
        }

        public void Log(LoggerEvent loggerEvent, string message)
        {
            switch (loggerEvent)
            {
                case LoggerEvent.Error:
                    AddMessage(loggerEvent, message);
                    if (ShowErrorMessage)
                    {
                        MessageBox.Show(message);
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
