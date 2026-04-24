using Plugin.BLE;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using System.Collections.ObjectModel;
using System.Text;

#if ANDROID
using Android;
using AndroidX.Core.Content;
using AndroidX.Core.App;
using Microsoft.Maui.ApplicationModel;
#endif

namespace OilDetectorApp;

public partial class MainPage : ContentPage
{
    private readonly Guid SERVICE_UUID = Guid.Parse("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");
    private readonly Guid TX_CHARACTERISTIC_UUID = Guid.Parse("6E400003-B5A3-F393-E0A9-E50E24DCCA9E");
    private readonly Guid RX_CHARACTERISTIC_UUID = Guid.Parse("6E400002-B5A3-F393-E0A9-E50E24DCCA9E");

    private IBluetoothLE _bluetoothLE;
    private IAdapter _adapter;
    private IDevice _connectedDevice;
    private ICharacteristic _notifyCharacteristic;

    private bool _isConnecting = false;
    private bool _isScanning = false;

    private int _messageCounter = 0;

    #region Android Permission Request

    private async Task CheckAndRequestPermissions()
    {
#if ANDROID
        var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();

        if (status != PermissionStatus.Granted)
        {
            status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        }

        if (status == PermissionStatus.Granted)
        {
            AddDataToUI("✅ Разрешения получены");
        }
        else
        {
            AddDataToUI("⚠️ Разрешения не получены");
        }
#endif
    }

    #endregion

    public MainPage()
    {
        InitializeComponent();
        InitializeBluetooth();

#if ANDROID
        CheckAndRequestPermissions();
#endif

        // Автоматически начинаем поиск и подключение при запуске
        Loaded += async (s, e) => await AutoConnectToDevice();
    }

    private void InitializeBluetooth()
    {
        _bluetoothLE = CrossBluetoothLE.Current;
        _adapter = CrossBluetoothLE.Current.Adapter;

        _adapter.DeviceDiscovered += OnDeviceDiscovered;
        _adapter.DeviceConnected += OnDeviceConnected;
        _adapter.DeviceDisconnected += OnDeviceDisconnected;

        CheckBluetooth();
    }

    private void CheckBluetooth()
    {
        if (_bluetoothLE.State == BluetoothState.On)
        {
            UpdateStatus("Bluetooth включен", Colors.Green);
        }
        else
        {
            UpdateStatus("Bluetooth выключен", Colors.Red);
        }
    }

    private void UpdateStatus(string message, Color color)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (StatusLabel != null)
            {
                StatusLabel.Text = message;
                StatusLabel.TextColor = color;
            }
        });
    }

    private void AddDataToUI(string data)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (DataContainer == null || EmptyDataLabel == null) return;

            if (EmptyDataLabel.IsVisible)
            {
                EmptyDataLabel.IsVisible = false;
                DataContainer.Children.Clear();
            }

            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var dataLabel = new Label
            {
                Text = $"[{timestamp}] {data}",
                TextColor = Colors.White,
                FontSize = 13,
                LineBreakMode = LineBreakMode.WordWrap
            };

            DataContainer.Children.Add(dataLabel);

            if (DataScrollView != null)
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    await Task.Delay(50);
                    await DataScrollView.ScrollToAsync(dataLabel, ScrollToPosition.End, true);
                });
            }

            _messageCounter++;
            if (CounterLabel != null)
            {
                CounterLabel.Text = $"Получено: {_messageCounter} сообщений";
            }
        });
    }

    // ========== АВТОМАТИЧЕСКОЕ ПОДКЛЮЧЕНИЕ ==========
    private async Task AutoConnectToDevice()
    {
        // Ждем включения Bluetooth
        if (_bluetoothLE.State != BluetoothState.On)
        {
            UpdateStatus("Ожидание включения Bluetooth...", Colors.Orange);
            await Task.Delay(2000);

            if (_bluetoothLE.State != BluetoothState.On)
            {
                UpdateStatus("Включите Bluetooth!", Colors.Red);
                return;
            }
        }

        UpdateStatus("Поиск ESP32...", Colors.Orange);
        AddDataToUI("🔍 Начинаю автоматический поиск ESP32...");

        // Сканируем 5 секунд
        _isScanning = true;
        await _adapter.StartScanningForDevicesAsync();
        await Task.Delay(5000);
        await _adapter.StopScanningForDevicesAsync();
        _isScanning = false;

        // Ищем ESP32 среди найденных устройств
        var devices = _adapter.DiscoveredDevices;
        IDevice targetDevice = null;

        if (devices == null || devices.Count == 0)
        {
            UpdateStatus("Устройства не найдены!", Colors.Red);
            AddDataToUI("❌ Устройства не найдены. Убедитесь, что ESP32 включен.");
            return;
        }

        foreach (var device in devices)
        {
            // ✅ ПРОВЕРКА: device не должен быть null
            if (device == null) continue;

            // ✅ ПРОВЕРКА: Name может быть null, используем безопасное получение
            string deviceName = device.Name;
            if (string.IsNullOrEmpty(deviceName)) continue;

            // Ищем по имени ESP32
            if (!string.IsNullOrEmpty(device.Name) &&
                (device.Name.Contains("ESP32") ||
                 device.Name.Contains("Oil_Detector") ||
                 device.Name.Contains("TSL2561")))
            {
                targetDevice = device;
                AddDataToUI($"✅ Найден ESP32: {device.Name}");
                break;
            }
        }

        if (targetDevice == null)
        {
            UpdateStatus("ESP32 не найден!", Colors.Red);
            AddDataToUI("❌ ESP32 не найден. Убедитесь, что устройство включено.");
            return;
        }

        // Подключаемся
        await ConnectToDevice(targetDevice);
    }

    private async Task ConnectToDevice(IDevice device)
    {
        if (_isConnecting)
        {
            AddDataToUI("⚠️ Уже выполняется подключение...");
            return;
        }

        _isConnecting = true;

        try
        {
            UpdateStatus($"Подключение к {device.Name}...", Colors.Orange);
            AddDataToUI($"🔌 Подключаюсь к {device.Name}...");

            // Подключаемся
            await _adapter.ConnectToDeviceAsync(device);
            _connectedDevice = device;

            // ✅ УВЕЛИЧИВАЕМ MTU (важно сделать до получения сервисов)
            AddDataToUI("📏 Запрос увеличения MTU...");
            try
            {
                var mtu = await _connectedDevice.RequestMtuAsync(517);
                AddDataToUI($"✅ MTU установлен: {mtu} байт");
            }
            catch (Exception ex)
            {
                AddDataToUI($"⚠️ Не удалось установить MTU: {ex.Message}");
            }

            // Небольшая задержка для стабилизации
            await Task.Delay(500);

            // Получаем сервисы
            var services = await _connectedDevice.GetServicesAsync();
            AddDataToUI($"📋 Найдено сервисов: {services.Count}");

            // Ищем наш сервис по UUID
            var targetService = services.FirstOrDefault(s => s.Id == SERVICE_UUID);

            if (targetService == null)
            {
                AddDataToUI($"❌ Сервис {SERVICE_UUID} не найден!");
                return;
            }

            AddDataToUI($"✅ Найден сервис: {targetService.Id}");

            // Получаем характеристики
            var characteristics = await targetService.GetCharacteristicsAsync();

            // Ищем TX характеристику
            var txCharacteristic = characteristics.FirstOrDefault(c => c.Id == TX_CHARACTERISTIC_UUID);

            if (txCharacteristic == null)
            {
                AddDataToUI($"❌ TX характеристика не найдена!");
                return;
            }

            _notifyCharacteristic = txCharacteristic;
            _notifyCharacteristic.ValueUpdated += OnDataReceived;

            // Подписываемся на уведомления
            await _notifyCharacteristic.StartUpdatesAsync();
            AddDataToUI($"✅ Подписка на уведомления активирована!");

            UpdateStatus("Подключен", Colors.Green);
            ConnectButton.IsEnabled = false;
            DisconnectButton.IsEnabled = true;
            ScanButton.IsEnabled = false;

            AddDataToUI("🟢 Подключено! Ожидание данных от ESP32...");
        }
        catch (Exception ex)
        {
            UpdateStatus("Ошибка подключения", Colors.Red);
            AddDataToUI($"❌ Ошибка: {ex.Message}");

            if (_notifyCharacteristic != null)
            {
                _notifyCharacteristic.ValueUpdated -= OnDataReceived;
                _notifyCharacteristic = null;
            }
            _connectedDevice = null;
        }
        finally
        {
            _isConnecting = false;
        }
    }

    private void OnDataReceived(object sender, CharacteristicUpdatedEventArgs e)
    {
        var bytes = e.Characteristic.Value;
        if (bytes != null && bytes.Length > 0)
        {
            var data = Encoding.UTF8.GetString(bytes);
            data = data.Trim().Replace("\0", "");

            if (!string.IsNullOrWhiteSpace(data))
            {
                AddDataToUI(data);
            }
        }
    }

    private void OnDeviceDiscovered(object sender, DeviceEventArgs args)
    {
        // Не добавляем в список, только логируем для отладки
        var device = args.Device;
        if (!string.IsNullOrEmpty(device.Name))
        {
            System.Diagnostics.Debug.WriteLine($"Найдено: {device.Name}");
        }
    }

    private void OnDeviceConnected(object sender, DeviceEventArgs args)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateStatus("Подключен", Colors.Green);
        });
    }

    private async void OnDeviceDisconnected(object sender, DeviceEventArgs args)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateStatus("Отключен", Colors.Red);
            ConnectButton.IsEnabled = true;
            DisconnectButton.IsEnabled = false;
            ScanButton.IsEnabled = true;

            AddDataToUI("🔴 Отключено от устройства");
        });

        _connectedDevice = null;
        _notifyCharacteristic = null;

        // Автоматически пробуем переподключиться через 5 секунд
        await Task.Delay(5000);
        if (_bluetoothLE.State == BluetoothState.On)
        {
            AddDataToUI("🔄 Попытка автоматического переподключения...");
            await AutoConnectToDevice();
        }
    }

    // ========== КНОПКИ УПРАВЛЕНИЯ ==========
    private async void OnScanClicked(object sender, EventArgs e)
    {
        await AutoConnectToDevice();
    }

    private async void OnConnectClicked(object sender, EventArgs e)
    {
        await AutoConnectToDevice();
    }

    private async void OnDisconnectClicked(object sender, EventArgs e)
    {
        if (_connectedDevice != null)
        {
            if (_notifyCharacteristic != null)
            {
                _notifyCharacteristic.ValueUpdated -= OnDataReceived;
                await _notifyCharacteristic.StopUpdatesAsync();
            }
            await _adapter.DisconnectDeviceAsync(_connectedDevice);
        }
    }

    private void OnClearClicked(object sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (DataContainer != null && EmptyDataLabel != null)
            {
                DataContainer.Children.Clear();
                EmptyDataLabel.IsVisible = true;
                _messageCounter = 0;
                if (CounterLabel != null)
                {
                    CounterLabel.Text = "Получено: 0 сообщений";
                }
            }
        });
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        if (_connectedDevice != null && _adapter != null)
        {
            await _adapter.DisconnectDeviceAsync(_connectedDevice);
        }
    }
}