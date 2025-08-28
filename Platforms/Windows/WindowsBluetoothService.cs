using System;
using System.Linq;
using System.Threading.Tasks;
using Plugin.BLE.Abstractions.Contracts;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace BleScannerMaui;

public partial class BluetoothService
{
    private async partial Task<bool> EnsureDevicePairedAsync(IDevice device)
    {
        if (device?.NativeDevice is BluetoothLEDevice bleDevice)
        {
            var devInfo = await DeviceInformation.CreateFromIdAsync(bleDevice.DeviceInformation.Id);

            if (!devInfo.Pairing.IsPaired)
            {
                var result = await devInfo.Pairing.PairAsync(DevicePairingProtectionLevel.EncryptionAndAuthentication);
                if (result.Status == DevicePairingResultStatus.Paired ||
                    result.Status == DevicePairingResultStatus.AlreadyPaired)
                {
                    _log.Append("Device paired successfully.");
                    return true;
                }
                _log.Append($"Pairing failed: {result.Status}");
                return false;
            }
            else
            {
                _log.Append("Device already paired. Skipping pairing step.");
                return true;
            }
        }
        else
        {
            _log.Append("NativeDevice is not a BluetoothLEDevice. Skipping pairing step.");
            return true;
        }
    }

    private async partial Task UnpairAsync(IDevice device)
    {
        try
        {
            _log.Append($"Device is: {device}");

            BluetoothLEDevice? bleDevice = null;

            if (device?.NativeDevice is BluetoothLEDevice nativeBleDevice)
            {
                bleDevice = nativeBleDevice;
                _log.Append("Using NativeDevice as BluetoothLEDevice for unpairing.");
            }
            else if (device != null)
            {
                var devices = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelector());
                var match = devices.FirstOrDefault(d =>
                    d.Name == device.Name ||
                    d.Id.Contains(device.Id.ToString(), StringComparison.OrdinalIgnoreCase));

                if (match != null)
                {
                    bleDevice = await BluetoothLEDevice.FromIdAsync(match.Id);
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
                var devInfo = await DeviceInformation.CreateFromIdAsync(bleDevice.DeviceInformation.Id);

                if (devInfo.Pairing.IsPaired)
                {
                    var result = await devInfo.Pairing.UnpairAsync();
                    if (result.Status == DeviceUnpairingResultStatus.Unpaired ||
                        result.Status == DeviceUnpairingResultStatus.AlreadyUnpaired)
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
    }
}