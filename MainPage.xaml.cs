using System;
using System.Collections.ObjectModel;
using Microsoft.Maui.Controls;
using Plugin.BLE.Abstractions.Contracts;
using Microsoft.Maui.ApplicationModel;

namespace BleScannerMaui
{
    public partial class MainPage : ContentPage
    {
        readonly IBluetoothService _bluetooth;
        readonly ILogService _log;

        ObservableCollection<DeviceViewModel> _devices = new();

        IDevice? _selectedDevice;

        public MainPage(IBluetoothService bluetoothService, ILogService logService)
        {
            InitializeComponent();

            _bluetooth = bluetoothService;
            _log = logService;

            DevicesCollection.ItemsSource = _devices;

            // subscribe to events
            _bluetooth.DeviceDiscovered += OnDeviceDiscovered;
            _bluetooth.StatusUpdated += OnStatusUpdated;
            _log.LogUpdated += OnLogUpdated;

            // init with empty log
            LogEditor.Text = _log.LogText;
        }

        void OnLogUpdated(string newest)
        {
            // always update UI on main thread
            MainThread.BeginInvokeOnMainThread(() =>
            {
                LogEditor.Text = newest;
                // scroll to bottom - set cursor at end
                LogEditor.CursorPosition = LogEditor.Text?.Length ?? 0;
            });
        }

        void OnDeviceDiscovered(IDevice device)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                // keep list of viewmodels
                if (!_devices.Any(d => d.Id == device.Id.ToString()))
                {
                    _devices.Add(new DeviceViewModel(device));
                }
            });
        }

        void OnStatusUpdated(string status)
        {
            _log.Append($"Status: {status}");
        }

        async void ScanButton_Clicked(object sender, EventArgs e)
        {
            await _bluetooth.StartScanAsync();
        }

        async void StopScanButton_Clicked(object sender, EventArgs e)
        {
            await _bluetooth.StopScanAsync();
        }

        void DevicesCollection_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedDevice = (e.CurrentSelection.FirstOrDefault() as DeviceViewModel)?.Device;
        }

        async void ConnectButton_Clicked(object sender, EventArgs e)
        {
            if (_selectedDevice == null)
            {
                _log.Append("No device selected.");
                return;
            }

            await _bluetooth.ConnectAsync(_selectedDevice);
        }

        async void DisconnectButton_Clicked(object sender, EventArgs e)
        {
            await _bluetooth.DisconnectAsync();
        }

        private void OnClearDevicesClicked(object sender, EventArgs e)
        {
            _devices.Clear();
            _bluetooth.ClearDiscoveredDevices(); // <-- Add this if available
            _log.Append("Device list cleared by user.");
        }

        void ClearLogButton_Clicked(object sender, EventArgs e)
        {
            _log.Clear();
        }
    }

    public class DeviceViewModel
    {
        public DeviceViewModel(IDevice device)
        {
            Name = string.IsNullOrEmpty(device.Name) ? "(no name)" : device.Name;
            Id = device.Id.ToString();
            Device = device;
        }

        public string Name { get; }
        public string Id { get; }
        public IDevice Device { get; }
    }
}
