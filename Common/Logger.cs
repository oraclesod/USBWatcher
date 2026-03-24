using System;
using System.Diagnostics;
using System.IO;

namespace USBWatcher.Common
{
    internal sealed class Logger
    {
        private readonly string _eventSource;
        private readonly string _filePath;
        private readonly string _componentName;

        public Logger(string eventSource, string filePath, string componentName)
        {
            _eventSource = eventSource ?? throw new ArgumentNullException(nameof(eventSource));
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
            _componentName = componentName ?? throw new ArgumentNullException(nameof(componentName));
        }

        public void Info(string message, int eventId)
            => Write(message, EventLogEntryType.Information, eventId);

        public void Warn(string message, int eventId)
            => Write(message, EventLogEntryType.Warning, eventId);

        public void Error(string message, int eventId)
            => Write(message, EventLogEntryType.Error, eventId);

        public void Write(string message, EventLogEntryType type, int eventId)
        {
            string finalMessage = $"[{_componentName}] {message}";

            try
            {
                EventLog.WriteEntry(_eventSource, finalMessage, type, eventId);
            }
            catch
            {
                AppendFileLog(finalMessage, type, eventId);
            }
        }

        private void AppendFileLog(string message, EventLogEntryType type, int eventId)
        {
            try
            {
                string? dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                File.AppendAllText(
                    _filePath,
                    $"{DateTime.Now:O} [{type}] [EventId:{eventId}] {message}{Environment.NewLine}");
            }
            catch
            {
                // Swallow logging failures to avoid recursive or fatal logging issues.
            }
        }
    }
}