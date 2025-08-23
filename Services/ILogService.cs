using System;

namespace BleScannerMaui
{
    public interface ILogService
    {
        event Action<string>? LogUpdated;
        string LogText { get; }
        void Append(string message);
        void Clear();
    }
}
