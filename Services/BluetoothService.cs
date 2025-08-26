using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using System.Collections.ObjectModel;


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

        private ICharacteristic _notifyCharacteristic;
        readonly ObservableCollection<IDevice> _devices = new();


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
            //_devices.Clear(); // Assuming _discoveredDevices is your internal list
        }

        // === Event handlers ===

        void Adapter_DeviceConnectionLost(object sender, DeviceErrorEventArgs e)
        {
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
            //_log.Append($"Device disconnected: {e.Device?.Name} ({e.Device?.Id})");
            //ConnectedDevice = null;
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
            // Only attempt to pair if NativeDevice is a BluetoothLEDevice
            if (device?.NativeDevice is Windows.Devices.Bluetooth.BluetoothLEDevice bleDevice)
            {
                var devInfo = await Windows.Devices.Enumeration.DeviceInformation.CreateFromIdAsync(
                    bleDevice.DeviceInformation.Id);

                if (!devInfo.Pairing.IsPaired)
                {
                    bool paired = await EnsurePairedAsync(device.Id.ToString(), device.NativeDevice);
                    if (!paired)
                    {
                        _log.Append("Pairing failed or canceled by user.");
                        return;
                    }
                    _log.Append("Device paired successfully.");
                }
                else
                {
                    _log.Append("Device already paired. Skipping pairing step.");
                }
            }
            else
            {
                _log.Append("NativeDevice is not a BluetoothLEDevice. Skipping pairing step.");
            }
#endif

            try
            {
                var parameters = new ConnectParameters(autoConnect: false, forceBleTransport: true);
                _log.Append("Connecting securely...");
                await _adapter.ConnectToDeviceAsync(device, parameters);
                // If connection is lost, event handler will handle retry
            }
            catch (Exception ex)
            {
                _log.Append($"Connect failed: {ex.Message}");
                // Optionally, you could retry here as well if you want to handle exceptions
            }
        }

        public async Task DisconnectAsync()
        {
            if (ConnectedDevice == null) return;

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
                    _log.Append($"Disconnecting from {deviceToUnpair.Name}...");
                    await UnpairAsync(deviceToUnpair);
                    //await _adapter.DisconnectDeviceAsync(deviceToUnpair);
                    _log.Append($"Disconnected from {deviceToUnpair.Name}");
                }

                _log.Append($"My device: {deviceToUnpair}");

                // 3. Optionally unpair (Windows only)
                

                // 4. Clear connected device reference
                ConnectedDevice = null;

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
        private async Task UnpairAsync(IDevice device)
        {
#if WINDOWS
try
{
    _log.Append($"Device is: {device}");
    
    Windows.Devices.Bluetooth.BluetoothLEDevice? bleDevice = null;

    if (device?.NativeDevice is Windows.Devices.Bluetooth.BluetoothLEDevice nativeBleDevice)
    {
        bleDevice = nativeBleDevice;
        _log.Append("Using NativeDevice as BluetoothLEDevice for unpairing.");
    }
    else if (device != null)
    {
        // Try to find the device by name or address
        var devices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelector());
        var match = devices.FirstOrDefault(d =>
            d.Name == device.Name ||
            d.Id.Contains(device.Id.ToString(), StringComparison.OrdinalIgnoreCase));

        if (match != null)
        {
            bleDevice = await Windows.Devices.Bluetooth.BluetoothLEDevice.FromIdAsync(match.Id);
            _log.Append($"Found device by enumeration: {match.Name} ({match.Id})");
        }
        else
        {
            _log.Append("Could not find matching device by enumeration.");
        }
    }
    else
    {
        _log.Append("Device is null. Cannot unpair.");
    }

    if (bleDevice != null)
    {
        var devInfo = await Windows.Devices.Enumeration.DeviceInformation.CreateFromIdAsync(
            bleDevice.DeviceInformation.Id);

        if (devInfo.Pairing.IsPaired)
        {
            var result = await devInfo.Pairing.UnpairAsync();
            if (result.Status == Windows.Devices.Enumeration.DeviceUnpairingResultStatus.Unpaired ||
                result.Status == Windows.Devices.Enumeration.DeviceUnpairingResultStatus.AlreadyUnpaired)
            {
                _log.Append($"Device {device.Name} unpaired successfully.");
            }
            else
            {
                _log.Append($"Unpair failed: {result.Status}");
            }
        }
        else
        {
            _log.Append($"Device {device.Name} was not paired.");
        }
    }
    else
    {
        _log.Append("BluetoothLEDevice is null. Cannot unpair.");
    }
}
catch (Exception ex)
{
    _log.Append($"Unpair error: {ex.Message}");
}
#else
_log.Append("Unpairing is only supported on Windows.");
#endif
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
