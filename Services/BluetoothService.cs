using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using System.Collections.ObjectModel;

namespace BleScannerMaui
{
    public partial class BluetoothService : IBluetoothService
    {
        readonly IAdapter _adapter;
        readonly IBluetoothLE _ble;
        readonly ILogService _log;

        private ICharacteristic _notifyCharacteristic;
        readonly ObservableCollection<IDevice> _devices = new();

        private bool _userInitiatedDisconnect = false;

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

        public void ClearDiscoveredDevices()
        {
            //_devices.Clear(); // Uncomment if you want to clear the list
        }

        // === Event handlers ===

        void Adapter_DeviceConnectionLost(object sender, DeviceErrorEventArgs e)
        {
            if (_userInitiatedDisconnect)
            {
                _userInitiatedDisconnect = false; // Reset for next time
                StatusUpdated?.Invoke("ConnectionLost");
                return;
            }

            StatusUpdated?.Invoke("ConnectionLost");

            if (_lastConnectAttemptDevice != null && _connectRetries < MaxConnectRetries)
            {
                _connectRetries++;
                _log.Append($"Connection lost. Retrying {_connectRetries}/{MaxConnectRetries}...");
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    await TryConnectAsync(_lastConnectAttemptDevice);
                });
            }
            else if (_lastConnectAttemptDevice != null)
            {
                var deviceToUnpair = _lastConnectAttemptDevice; // Capture reference
                _log.Append("Failed to connect after 3 attempts. Unpairing device...");
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    await UnpairAsync(deviceToUnpair);
                    _lastConnectAttemptDevice = null; // Only clear after unpair completes
                    _connectRetries = 0;
                });
            }
        }

        void Adapter_DeviceDisconnected(object sender, DeviceEventArgs e)
        {
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
                _log.Append("Scanning canceled.");
            }
            catch (Exception ex)
            {
                _log.Append($"Stop scan failed: {ex.Message}");
            }
        }

        private int _connectRetries = 0;
        private const int MaxConnectRetries = 3;
        private IDevice? _lastConnectAttemptDevice;

        public async Task ConnectAsync(IDevice device)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            _lastConnectAttemptDevice = device;
            _connectRetries = 0;
            await TryConnectAsync(device);
        }

        private async Task TryConnectAsync(IDevice device)
        {
#if WINDOWS
            bool paired = await EnsureDevicePairedAsync(device);
            if (!paired)
            {
                _log.Append("Pairing failed or canceled by user.");
                return;
            }
#endif
            try
            {
                var parameters = new ConnectParameters(autoConnect: false, forceBleTransport: true);
                _log.Append("Connecting securely...");
                await _adapter.ConnectToDeviceAsync(device, parameters);
            }
            catch (Exception ex)
            {
                _log.Append($"Connect failed: {ex.Message}");
            }
        }

        public async Task DisconnectAsync()
        {
            if (ConnectedDevice == null) return;

            _userInitiatedDisconnect = true; // Set the flag here

            var deviceToUnpair = ConnectedDevice; // Capture reference before disconnect

            _log.Append($"Disconnecting from {deviceToUnpair.Name ?? deviceToUnpair.Id.ToString()}...");

            try
            {
                // 1. Stop notifications if active
                if (_notifyCharacteristic != null)
                {
                    try
                    {
                        await _notifyCharacteristic.StopUpdatesAsync();
                        _log.Append("Stopped notifications.");
                    }
                    catch (Exception ex)
                    {
                        _log.Append($"Error stopping notifications: {ex.Message}");
                    }
                    _notifyCharacteristic = null;
                }

                // 2. Disconnect from the device
                if (_adapter != null && deviceToUnpair != null)
                {
                    await _adapter.DisconnectDeviceAsync(deviceToUnpair);
                    _log.Append($"Disconnected from {deviceToUnpair.Name}");
                    await UnpairAsync(deviceToUnpair);
                }

                _log.Append($"My device: {deviceToUnpair}");

                // 4. Clear connected device reference
                ConnectedDevice = null;
                _lastConnectAttemptDevice = null;

                // 5. Optionally clear device list
                _devices.Clear();
                _log.Append("Device list cleared after disconnect.");
            }
            catch (Exception ex)
            {
                _log.Append($"Disconnect error: {ex.Message}");
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
        private partial Task<bool> EnsureDevicePairedAsync(IDevice device);
        private partial Task UnpairAsync(IDevice device);
#else
        private partial Task<bool> EnsureDevicePairedAsync(IDevice device) => Task.FromResult(true);
        private partial Task UnpairAsync(IDevice device) => Task.CompletedTask;
#endif
    }
}
