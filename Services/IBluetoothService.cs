using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Plugin.BLE.Abstractions.Contracts;

namespace BleScannerMaui
{
    public interface IBluetoothService
    {
        event Action<IDevice>? DeviceDiscovered;
        event Action<string>? StatusUpdated;
        IReadOnlyList<IDevice> DiscoveredDevices { get; }

        Task StartScanAsync();
        Task StopScanAsync();

        Task ConnectAsync(IDevice device);
        Task DisconnectAsync();

        IDevice? ConnectedDevice { get; }
        bool IsScanning { get; }
        bool IsConnected { get; }
    }
}
