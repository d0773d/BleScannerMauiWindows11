using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Plugin.BLE;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using Microsoft.Maui.ApplicationModel;
using Plugin.BLE.Abstractions;


#if WINDOWS
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
#endif

namespace BleScannerMaui
{
    public class BluetoothService : IBluetoothService
    {
        readonly IAdapter _adapter;
        readonly IBluetoothLE _ble;
        readonly ILogService _log;
        readonly List<IDevice> _devices = new();

        public BluetoothService(ILogService log)
        {
            _log = log;
            _ble = CrossBluetoothLE.Current;
            _adapter = _ble.Adapter;

            // Wire up events
            _adapter.DeviceDiscovered += Adapter_DeviceDiscovered;
            _adapter.DeviceConnected += Adapter_DeviceConnected;
            _adapter.DeviceDisconnected += Adapter_DeviceDisconnected;
            _adapter.DeviceConnectionLost += Adapter_DeviceConnectionLost;
        }

        public IReadOnlyList<IDevice> DiscoveredDevices => _devices;

        public event Action<IDevice>? DeviceDiscovered;
        public event Action<string>? StatusUpdated;

        public bool IsScanning { get; private set; } = false;
        public bool IsConnected => ConnectedDevice != null;
        public IDevice? ConnectedDevice { get; private set; }

        // === Event handlers ===

        void Adapter_DeviceConnectionLost(object sender, DeviceErrorEventArgs e)
        {
            _log.Append($"Connection lost: {e.Device?.Name} ({e.Device?.Id})");
            ConnectedDevice = null;
            StatusUpdated?.Invoke("ConnectionLost");
        }

        void Adapter_DeviceDisconnected(object sender, DeviceEventArgs e)
        {
            _log.Append($"Device disconnected: {e.Device?.Name} ({e.Device?.Id})");
            ConnectedDevice = null;
            StatusUpdated?.Invoke("Disconnected");
        }

        void Adapter_DeviceConnected(object sender, DeviceEventArgs e)
        {
            _log.Append($"Device connected: {e.Device?.Name} ({e.Device?.Id})");
            ConnectedDevice = e.Device;
            StatusUpdated?.Invoke("Connected");

            _ = DiscoverAndSubscribeAsync(e.Device); // fire & forget
        }

        void Adapter_DeviceDiscovered(object sender, DeviceEventArgs e)
        {
            lock (_devices)
            {
                if (!_devices.Any(d => d.Id == e.Device.Id))
                {
                    _devices.Add(e.Device);
                    _log.Append($"Discovered: {e.Device.Name ?? "(no name)"} [{e.Device.Id}]");
                    DeviceDiscovered?.Invoke(e.Device);
                }
            }
        }

        // === Public API ===

        public async Task StartScanAsync()
        {
            if (IsScanning) return;
            _devices.Clear();
            _log.Append("Starting scan...");
            IsScanning = true;
            StatusUpdated?.Invoke("Scanning");
            try
            {
                await _adapter.StartScanningForDevicesAsync();
            }
            catch (Exception ex)
            {
                _log.Append($"Scan failed: {ex.Message}");
            }
            finally
            {
                IsScanning = false;
                StatusUpdated?.Invoke("ScanStopped");
                _log.Append("Scan stopped.");
            }
        }

        public async Task StopScanAsync()
        {
            if (!IsScanning) return;
            try
            {
                await _adapter.StopScanningForDevicesAsync();
                IsScanning = false;
                StatusUpdated?.Invoke("ScanStopped");
                _log.Append("Scanning cancelled.");
            }
            catch (Exception ex)
            {
                _log.Append($"Stop scan failed: {ex.Message}");
            }
        }

        public async Task ConnectAsync(IDevice device)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));

            try
            {
                _log.Append($"Ensuring secure pairing with {device.Name ?? device.Id.ToString()}...");

#if WINDOWS
                bool paired = await EnsurePairedAsync(device.Id.ToString(), device.NativeDevice);
                if (!paired)
                {
                    _log.Append("Pairing failed or canceled by user.");
                    return;
                }
                _log.Append("Device paired successfully.");
#endif

                var parameters = new ConnectParameters(autoConnect: false, forceBleTransport: true);
                _log.Append("Connecting securely...");
                await _adapter.ConnectToDeviceAsync(device, parameters);
            }
            catch (Exception ex)
            {
                _log.Append($"Secure connect failed: {ex.Message}");
            }
        }

        public async Task DisconnectAsync()
        {
            if (ConnectedDevice == null)
            {
                _log.Append("No device to disconnect.");
                return;
            }

            try
            {
                _log.Append($"Disconnecting {ConnectedDevice.Name ?? ConnectedDevice.Id.ToString()}...");
                await _adapter.DisconnectDeviceAsync(ConnectedDevice);
                ConnectedDevice = null;
                StatusUpdated?.Invoke("Disconnected");
            }
            catch (Exception ex)
            {
                _log.Append($"Disconnect failed: {ex.Message}");
            }
        }

        // === Private helpers ===

        async Task DiscoverAndSubscribeAsync(IDevice device)
        {
            try
            {
                _log.Append("Discovering target service...");

                Guid serviceGuid = Guid.Parse("A7EEDF2C-DA8C-4CB5-A9C5-5151C78B0057");
                Guid writeGuid = Guid.Parse("A7EEDF2C-DA90-4CB5-A9C5-5151C78B0057");
                Guid notifyGuid = Guid.Parse("A7EEDF2C-DA91-4CB5-A9C5-5151C78B0057");

                var service = await device.GetServiceAsync(serviceGuid);
                if (service == null)
                {
                    _log.Append("Target service not found.");
                    return;
                }
                _log.Append($"Found target service: {service.Id}");

                var writeChar = await service.GetCharacteristicAsync(writeGuid);
                var notifyChar = await service.GetCharacteristicAsync(notifyGuid);

                if (notifyChar == null)
                {
                    _log.Append("Notify characteristic not found.");
                    return;
                }

                if (notifyChar.CanUpdate)
                {
                    _log.Append($"Subscribing to notify characteristic {notifyChar.Id}...");
                    notifyChar.ValueUpdated += Characteristic_ValueUpdated;
                    await notifyChar.StartUpdatesAsync();
                    _log.Append("Subscribed successfully.");
                }
                else
                {
                    _log.Append("Notify characteristic not update-capable.");
                    return;
                }

                if (writeChar != null && writeChar.CanWrite)
                {
                    _log.Append($"Writing 0x01 to write characteristic {writeChar.Id}...");
                    var data = new byte[] { 0x01 };
                    await writeChar.WriteAsync(data);
                    _log.Append("Write successful.");
                }
                else
                {
                    _log.Append("Write characteristic not found or not writable.");
                }
            }
            catch (Exception ex)
            {
                _log.Append($"Discover/Subscribe/Write failed: {ex.Message}");
            }
        }

        void Characteristic_ValueUpdated(object sender, CharacteristicUpdatedEventArgs e)
        {
            try
            {
                var data = e.Characteristic.Value;
                string hex = data != null ? BitConverter.ToString(data) : "(null)";
                var readable = data != null ? System.Text.Encoding.UTF8.GetString(data) : "";
                _log.Append($"Notification from {e.Characteristic.Id}: HEX={hex} ASCII='{readable}'");
            }
            catch (Exception ex)
            {
                _log.Append($"Notification handling error: {ex.Message}");
            }
        }

#if WINDOWS
        async Task<bool> EnsurePairedAsync(string deviceId, object nativeDevice)
        {
            try
            {
                // On Windows, Plugin.BLE's device.NativeDevice is a BluetoothLEDevice
                if (nativeDevice is BluetoothLEDevice bleDevice)
                {
                    var devInfo = await DeviceInformation.CreateFromIdAsync(bleDevice.DeviceInformation.Id);

                    if (devInfo.Pairing.IsPaired)
                    {
                        _log.Append("Device already paired.");
                        return true;
                    }

                    _log.Append("Pairing with device (encryption + authentication required)...");
                    var result = await devInfo.Pairing.PairAsync(
                        DevicePairingProtectionLevel.EncryptionAndAuthentication);

                    if (result.Status == DevicePairingResultStatus.Paired ||
                        result.Status == DevicePairingResultStatus.AlreadyPaired)
                    {
                        return true;
                    }

                    _log.Append($"Pairing failed: {result.Status}");
                    return false;
                }
                else
                {
                    _log.Append("NativeDevice is not a BluetoothLEDevice. Cannot pair.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _log.Append($"EnsurePairedAsync error: {ex.Message}");
                return false;
            }
        }
#endif
    }
}
