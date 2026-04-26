using Plugin.BLE;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using System.Text;
using Plugin.BLE.Abstractions;


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
    private readonly Guid RX_CHARACTERISTIC_UUID = Guid.Parse("6E400002-B5A3-F393-E0A9-E50E24DCCA9E");
    private readonly Guid TX_CHARACTERISTIC_UUID = Guid.Parse("6E400003-B5A3-F393-E0A9-E50E24DCCA9E");

    private IBluetoothLE _bluetoothLE;
    private IAdapter _adapter;
    private IDevice _connectedDevice;
    private ICharacteristic _notifyCharacteristic;
    private ICharacteristic _writeCharacteristic;

    private bool _isScanning = false;
    private bool _isConnecting = false;
    private bool _isSendingCommand = false;

    #region Android Permission Request
    #if ANDROID
    private async Task CheckAndRequestPermissions()
    {

        var locationPermission = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
        if (locationPermission != PermissionStatus.Granted)
        {
            await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        }

        var BlePermission = await Permissions.CheckStatusAsync<Permissions.Bluetooth>();
        if (BlePermission != PermissionStatus.Granted)
        {
            await Permissions.RequestAsync<Permissions.Bluetooth>();
        }
    }
    #endif
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

        _adapter.DeviceConnected += OnDeviceConnected;
        _adapter.DeviceDisconnected += OnDeviceDisconnected;

        CheckBluetooth();
    }

    private void CheckBluetooth()
    {
        if (_bluetoothLE.State == BluetoothState.On)
        {
            UpdateStatus("Bluetooth включен");
        }
        else
        {
            UpdateStatus("Bluetooth выключен");
        }
    }

    private void UpdateStatus(string message, bool showBorder=false)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (StatusLabel != null)
            {
                StatusLabel.Text = message;
                StatusLabel.TextColor = (Color)Resources["ColorTextSecondary"];

                if (showBorder) StatusBorder.StrokeThickness = 3.0f;
                else StatusBorder.StrokeThickness = 0.0f;
            }
        });
    }


    // ========== СООБЩЕНИЕ, ЧТО УСТРОЙСТВО НЕ НАЙДЕНО ==========
    private void SendNotFound()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateStatus("Поиск устройства...");
            AddDataToUI("❗ Убедитесь, что устройство включено. Продолжаем поиск...🔎");
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

            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var dataLabel = new Label
            {
                Text = $"[{timestamp}] {data}",
                TextColor = Colors.White,
                FontSize = 13,
                LineBreakMode = LineBreakMode.WordWrap
            };

            DataContainer.Children.Add(dataLabel);

            // ✅ Ограничиваем количество элементов
            while (DataContainer.Children.Count > 20)
            {
                // Удаляем самый старый элемент (первый в списке)
                DataContainer.Children.RemoveAt(0);
            }

            if (DataScrollView != null)
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    await Task.Delay(50);
                    // Скролл в самый низ
                    await DataScrollView.ScrollToAsync(dataLabel, ScrollToPosition.End, true);
                });
            }
        });
    }


    // ========== АВТОМАТИЧЕСКОЕ ПОДКЛЮЧЕНИЕ ==========
    private async Task AutoConnectToDevice()
    {
        // Ждем включения Bluetooth
        UpdateStatus("Ожидание Bluetooth...");
        while (true)
        {
            if (_bluetoothLE.State != BluetoothState.On)
            {
                await Task.Delay(2000);
            }
            else {
                break;
            }
        }

        UpdateStatus("Поиск устройства...");


        // Создаем фильтр для сканирования по Service UUID
        var scanFilterOptions = new ScanFilterOptions
        {
            DeviceNames = ["ESP32_Oil_Detector_DonskoyPN"],
        };

        while (true)
        {
            // Сканируем 3 секунды
            _adapter.ScanTimeout = 3000;
            await _adapter.StartScanningForDevicesAsync();
            await Task.Delay(3000);
            await _adapter.StopScanningForDevicesAsync();

            // Ищем ESP32 среди найденных устройств
            var devices = _adapter.DiscoveredDevices;
            IDevice targetDevice = null;

            if (devices == null || devices.Count == 0)
            {
                SendNotFound();
                continue;
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
                     device.Name.Contains("Oil_Detector")))
                {
                    targetDevice = device;
                    break;
                }
            }

            if (targetDevice == null)
            {
                SendNotFound();
                continue;
            }

            // Подключаемся
            await ConnectToDevice(targetDevice);
            break;
        }
    }


    // ========== ПОДКЛЮЧЕНИЕ К УСТРОЙСТВУ ==========
    private async Task ConnectToDevice(IDevice device)
    {
        if (_isConnecting)
        {
            return;
        }

        _isConnecting = true;

        try
        {
            UpdateStatus($"Подключение...");

            // Подключаемся
            await _adapter.ConnectToDeviceAsync(device);
            _connectedDevice = device;

            // ✅ УВЕЛИЧИВАЕМ MTU (важно сделать до получения сервисов)
            try
            {
                var mtu = await _connectedDevice.RequestMtuAsync(517);
            }
            catch (Exception ex)
            {
                AddDataToUI($"⚠️ Не удалось установить MTU: {ex.Message}");
            }

            // Небольшая задержка для стабилизации
            await Task.Delay(500);

            // Получаем сервисы
            var services = await _connectedDevice.GetServicesAsync();

            // Ищем наш сервис по UUID
            var targetService = services.FirstOrDefault(s => s.Id == SERVICE_UUID);

            if (targetService == null)
            {
                AddDataToUI($"❌ Сервис {SERVICE_UUID} не найден!");
                return;
            }

            // Получаем характеристики
            var characteristics = await targetService.GetCharacteristicsAsync();

            // Ищем TX характеристику
            var txCharacteristic = characteristics.FirstOrDefault(c => c.Id == TX_CHARACTERISTIC_UUID);

            if (txCharacteristic == null)
            {
                AddDataToUI("❌ TX характеристика не найдена!");
                return;
            }

            // Ищем RX характеристику
            var rxCharacteristic = characteristics.FirstOrDefault(c => c.Id == RX_CHARACTERISTIC_UUID);

            if (rxCharacteristic == null)
            {
                AddDataToUI("❌ RX характеристика не найдена!");
                return;
            }

            _notifyCharacteristic = txCharacteristic;
            _writeCharacteristic = rxCharacteristic;
            _notifyCharacteristic.ValueUpdated += OnDataReceived;

            // Подписываемся на уведомления
            await _notifyCharacteristic.StartUpdatesAsync();
        }
        catch (Exception ex)
        {
            UpdateStatus("Ошибка подключения");
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
                if (data.Contains("Led"))
                {
                    string[] refinedData = data.Split("|");
                    // Создаём словарь
                    var dict = new Dictionary<string, string>();
                    foreach (string pair in refinedData)
                    {
                        string[] pairValues = pair.Split(':');
                        dict[pairValues[0]] = pairValues[1];
                    }

                    string threshold = dict["Threshold"].Replace(".", ",");
                    string feedString = " ";

                    foreach (string key in dict.Keys)
                    {
                        // Ключи датчиков
                        if (key.Contains("Led"))
                        {
                            feedString += key.Replace("Led", "Датчик ") + ": " + dict[key].PadRight(10) + " ";
                            string lightLevel = dict[key].Replace(".", ",");
                            switch (key) {
                                case "Led1":
                                    if (float.Parse(lightLevel) < float.Parse(threshold))
                                    {
                                        SetLedColorWithGradient(Led1, (Color)Resources["ColorLedBlue"]);
                                    }
                                    else SetLedColorWithGradient(Led1, (Color)Resources["ColorLedRed"]);
                                    break;
                                case "Led2":
                                    if (float.Parse(lightLevel) < float.Parse(threshold))
                                    {
                                        SetLedColorWithGradient(Led2, (Color)Resources["ColorLedBlue"]);
                                    }
                                    else SetLedColorWithGradient(Led2, (Color)Resources["ColorLedRed"]);
                                    break;
                                case "Led3":
                                    if (float.Parse(lightLevel) < float.Parse(threshold))
                                    {
                                        SetLedColorWithGradient(Led3, (Color)Resources["ColorLedBlue"]);
                                    }
                                    else SetLedColorWithGradient(Led3, (Color)Resources["ColorLedRed"]);
                                    break;
                                case "Led4":
                                    if (float.Parse(lightLevel) < float.Parse(threshold))
                                    {
                                        SetLedColorWithGradient(Led4, (Color)Resources["ColorLedBlue"]);
                                    }
                                    else SetLedColorWithGradient(Led4, (Color)Resources["ColorLedRed"]);
                                    break;
                                default:
                                    break;
                            }
                        }
                    }

                    feedString += "Порог: " + dict["Threshold"] + "\n";
                    feedString += "Тревога: " + (dict["IsAlarm"] == "true" ? "Да" : "Нет") + "\n";
                    feedString += "Чувствительность: " + dict["MTreg"] + "\n";

                    if (dict["IsAlarm"] == "true")
                    {
                        UpdateStatus("⚠ ОБНАРУЖЕНА НЕФТЬ ⚠", true);
                    }
                    else
                    {
                        UpdateStatus("✔ Можно работать ✔");
                    }
                    AddDataToUI(feedString);
                }
                else AddDataToUI(data);

            }
        }
    }

    private async void OnDeviceConnected(object sender, DeviceEventArgs args)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateStatus("Подключен");
        });
    }

    private async void OnDeviceDisconnected(object sender, DeviceEventArgs args)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateStatus("Потеря соединения");
        });

        _connectedDevice = null;
        _notifyCharacteristic = null;

        while (true)
        {
            // Автоматически пробуем переподключиться через 5 секунд
            await Task.Delay(5000);
            if (_connectedDevice != null)
            {
                break;
            }
            if (_bluetoothLE.State == BluetoothState.On)
            {
                AddDataToUI("🔄 Попытка автоматического переподключения...");
                await AutoConnectToDevice();
            }
        }
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        if (_connectedDevice != null && _adapter != null)
        {
            await _adapter.DisconnectDeviceAsync(_connectedDevice);
        }
    }


    // ========== ИЗМЕНЕНИЕ ЦВЕТА ДАТЧИКА ==========
    public void SetLedColorWithGradient(Border led, Color baseColor)
    {
        // ✅ Принудительно выполняем в UI потоке
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // Создаем градиент на основе базового цвета
            var gradient = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };

            // Цвета градиента: светлый вверху-слева, темный внизу-справа
            gradient.GradientStops.Add(new GradientStop(baseColor, 0.0f)); // исходный цвет
            gradient.GradientStops.Add(new GradientStop(baseColor.MultiplyAlpha(0.8f), 0.3f));
            gradient.GradientStops.Add(new GradientStop(baseColor.MultiplyAlpha(0.6f), 0.6f));
            gradient.GradientStops.Add(new GradientStop(baseColor.MultiplyAlpha(0.4f), 1.0f)); // самый темный

            led.Background = gradient;

            // Создаем новую тень с цветом свечения
            led.Shadow = new Shadow
            {
                Brush = baseColor,
                Radius = 16,
                Offset = new Point(0, 0),
                Opacity = 1.0f
            };
        });
    }


    // ========== ОБРАБОТЧИК ПОЛЗУНКА ЧУВСТВИТЕЛЬНОСТИ ==========
    private async void SensetivityValueChanged(object sender, ValueChangedEventArgs e)
    {
        var slider = (Slider)sender;
        int newValue = (int)Math.Round(e.NewValue);

        // Обновляем отображаемое значение (если есть Label)
        SensetivityValueLabel.Text = newValue.ToString();

        // Блокируем повторные вызовы во время отправки
        if (_isSendingCommand) return;
        _isSendingCommand = true;

        try
        {
            // Формируем команду: T + значение
            string command = $"T{newValue}";

            // Отправляем на ESP32
            await SendBLEData(command);
        }
        catch (Exception ex)
        {
            AddDataToUI($"❌ Ошибка отправки: {ex.Message}");
        }
        finally
        {
            _isSendingCommand = false;
        }
    }


    // ========== ОБРАБОТЧИК ПОЛЗУНКА ЧУВСТВИТЕЛЬНОСТИ ==========
    private async void ThresholdValueChanged(object sender, ValueChangedEventArgs e)
    {
        var slider = (Slider)sender;
        int newValue = (int)Math.Round(e.NewValue);

        // Обновляем отображаемое значение (если есть Label)
        ThresholdValueLabel.Text = newValue.ToString();

        // Блокируем повторные вызовы во время отправки
        if (_isSendingCommand) return;
        _isSendingCommand = true;

        try
        {
            // Формируем команду: S + значение
            string command = $"S{newValue}";

            // Отправляем на ESP32
            await SendBLEData(command);
        }
        catch (Exception ex)
        {
            AddDataToUI($"❌ Ошибка отправки: {ex.Message}");
        }
        finally
        {
            _isSendingCommand = false;
        }
    }

    // ========== ОТПРАВКА ДАННЫХ ПО BLE ==========
    private async Task SendBLEData(string data)
    {
        if (_writeCharacteristic == null)
        {
            AddDataToUI("❌ BLE характеристика не инициализирована");
            return;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(data);
            await _writeCharacteristic.WriteAsync(bytes);
            //AddDataToUI($"✅ Чувствительность изменена");
        }
        catch (Exception ex)
        {
            AddDataToUI($"❌ Ошибка BLE записи: {ex.Message}");
            throw;
        }
    }
}