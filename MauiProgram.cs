using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;
#if WINDOWS
using Windows.Devices.Bluetooth;
#endif

namespace BleScannerMaui
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>();

            // Register services for DI
            builder.Services.AddSingleton<ILogService, LogService>();
            builder.Services.AddSingleton<IBluetoothService, BluetoothService>();

            // Register MainPage so DI can inject services into its constructor
            builder.Services.AddSingleton<MainPage>();

            return builder.Build();
        }
    }
}
