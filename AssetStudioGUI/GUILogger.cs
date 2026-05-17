using AssetStudio;
using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace AssetStudioGUI
{
    class GUILogger : ILogger
    {
        public bool ShowErrorMessage;
        private readonly Action<string> action;
        private readonly List<string> errors = new List<string>();
        private readonly object errorsLock = new object();

        public GUILogger(Action<string> action)
        {
            this.action = action;
        }

        public void Log(LoggerEvent loggerEvent, string message)
        {
            switch (loggerEvent)
            {
                case LoggerEvent.Error:
                    lock (errorsLock)
                    {
                        errors.Add(message);
                    }
                    if (ShowErrorMessage)
                    {
                        MessageBox.Show(message);
                    }
                    else
                    {
                        action("Error logged. Export errors.txt to inspect details.");
                    }
                    break;
                default:
                    action(message);
                    break;
            }
        }

        public string[] GetErrors()
        {
            lock (errorsLock)
            {
                return errors.ToArray();
            }
        }

        public void ClearErrors()
        {
            lock (errorsLock)
            {
                errors.Clear();
            }
        }
    }
}
