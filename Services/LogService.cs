using System;
using System.Text;
using Microsoft.Maui.ApplicationModel;

namespace BleScannerMaui
{
    public class LogService : ILogService
    {
        readonly StringBuilder _sb = new();

        public event Action<string>? LogUpdated;

        public string LogText => _sb.ToString();

        void Fire()
        {
            // Ensure update occurs on UI thread
            MainThread.BeginInvokeOnMainThread(() => LogUpdated?.Invoke(_sb.ToString()));
        }

        public void Append(string message)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            _sb.AppendLine($"[{timestamp}] {message}");
            Fire();
        }

        public void Clear()
        {
            _sb.Clear();
            Fire();
        }
    }
}
