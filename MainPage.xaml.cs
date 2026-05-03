using Plugin.BLE;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using System.Text;
using Plugin.BLE.Abstractions;
using System.Diagnostics;

#if ANDROID
using Android;
using AndroidX.Core.Content;
using AndroidX.Core.App;
using Microsoft.Maui.ApplicationModel;
#endif

namespace OilDetectorApp;

public partial class MainPage : ContentPage, IDisposable
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

    // Для отслеживания таймаута данных
    private DateTime _lastDataReceivedTime;
    private Timer _dataTimeoutTimer;
    private const int DATA_TIMEOUT_SECONDS = 5;


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

#if ANDROID
    private async Task<bool> EnsurePermissionsAndLocation()
    {
        try
        {
            var activity = Platform.CurrentActivity;
            if (activity == null)
            {
                AddDataToUI("❌ Activity не найдена");
                return false;
            }

            List<string> permissionsToRequest = new List<string>();

            // Для Android 12+ (API 31+)
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                if (ContextCompat.CheckSelfPermission(activity, Android.Manifest.Permission.BluetoothScan)
                    != (int)Android.Content.PM.Permission.Granted)
                {
                    permissionsToRequest.Add(Android.Manifest.Permission.BluetoothScan);
                }

                if (ContextCompat.CheckSelfPermission(activity, Android.Manifest.Permission.BluetoothConnect)
                    != (int)Android.Content.PM.Permission.Granted)
                {
                    permissionsToRequest.Add(Android.Manifest.Permission.BluetoothConnect);
                }
            }

            // Для всех версий Android — разрешение на геолокацию
            if (ContextCompat.CheckSelfPermission(activity, Android.Manifest.Permission.AccessFineLocation)
                != (int)Android.Content.PM.Permission.Granted)
            {
                permissionsToRequest.Add(Android.Manifest.Permission.AccessFineLocation);
            }

            // Запрашиваем разрешения, если есть что запрашивать
            if (permissionsToRequest.Any())
            {
                AddDataToUI($"📱 Запрос {permissionsToRequest.Count} разрешений...");
                ActivityCompat.RequestPermissions(activity, permissionsToRequest.ToArray(), 1001);
                await Task.Delay(2000); // Даем время на ответ
            }

            // Проверяем результат
            bool allGranted = true;

            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                var scanGranted = ContextCompat.CheckSelfPermission(activity, Android.Manifest.Permission.BluetoothScan)
                    == (int)Android.Content.PM.Permission.Granted;
                var connectGranted = ContextCompat.CheckSelfPermission(activity, Android.Manifest.Permission.BluetoothConnect)
                    == (int)Android.Content.PM.Permission.Granted;

                AddDataToUI($"📱 Bluetooth Scan: {(scanGranted ? "✅" : "❌")}");
                AddDataToUI($"📱 Bluetooth Connect: {(connectGranted ? "✅" : "❌")}");

                allGranted = scanGranted && connectGranted;
            }

            var locationGranted = ContextCompat.CheckSelfPermission(activity, Android.Manifest.Permission.AccessFineLocation)
                == (int)Android.Content.PM.Permission.Granted;
            AddDataToUI($"📱 Location: {(locationGranted ? "✅" : "❌")}");

            allGranted = allGranted && locationGranted;

            if (!allGranted)
            {
                AddDataToUI("⚠️ Не все разрешения получены. BLE может не работать.");
                return false;
            }

            // Проверяем, включена ли геолокация
            var locationManager = (Android.Locations.LocationManager)Android.App.Application.Context.GetSystemService(Android.Content.Context.LocationService);
            var isLocationEnabled = locationManager?.IsLocationEnabled == true;

            if (!isLocationEnabled)
            {
                AddDataToUI("❌ Геолокация выключена! Включите в настройках.");
                AddDataToUI("   Настройки → Местоположение → Включить");
                return false;
            }

            AddDataToUI("✅ Все разрешения получены, геолокация включена");
            return true;
        }
        catch (Exception ex)
        {
            AddDataToUI($"⚠️ Ошибка при проверке разрешений: {ex.Message}");
            return false;
        }
    }
#else
private async Task<bool> EnsurePermissionsAndLocation()
{
    // Для не-Android платформ всегда true
    return await Task.FromResult(true);
}
#endif
    #endregion

    public MainPage()
    {
        InitializeComponent();
        _ = AnimateMainBorder();
        _ =AnimateAlarmBorder();
        InitializeBluetooth();

#if ANDROID
        _ = CheckAndRequestPermissions();
        Task.Delay(10);
#endif

        // Автоматически начинаем поиск и подключение при запуске
        _ = AutoConnectToDevice();
    }

    public void Dispose()
    {
        // Принудительная чистка
        _dataTimeoutTimer?.Dispose();
        _notifyCharacteristic?.StopUpdatesAsync().ConfigureAwait(false);
        _adapter?.DisconnectDeviceAsync(_connectedDevice).ConfigureAwait(false);
    }

    public async Task DisconnectAndReset()
    {
        try
        {
            // Отписываемся от событий
            if (_notifyCharacteristic != null)
            {
                _notifyCharacteristic.ValueUpdated -= OnDataReceived;
                try
                {
                    await _notifyCharacteristic.StopUpdatesAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Ошибка: {ex.Message}");
                }
                _notifyCharacteristic = null!;
            }

            // Разрываем соединение
            if (_connectedDevice != null && _adapter != null)
            {
                await _adapter.DisconnectDeviceAsync(_connectedDevice);
                await Task.Delay(500);
            }

            _connectedDevice = null!;
            _writeCharacteristic = null!;

            _dataTimeoutTimer?.Dispose();
            _dataTimeoutTimer = null!;

            _isScanning = false;
            _isConnecting = false;
            _isSendingCommand = false;

            ResetGui();
            _ = TryReconnect();
        }
        catch (Exception) 
        {
            AddDataToUI("Ошибка при отключении от устройства. Перезапустите приложение и устройство.");
        }
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
            UpdateConnectionStatus("Bluetooth включен");
        }
        else
        {
            UpdateConnectionStatus("Bluetooth выключен");
        }
    }


    // ========== ПОМЕНЯТЬ СТАТУС СОЕДИНЕНИЯ ==========
    private void UpdateConnectionStatus(string message)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (ConnectionStatusLabel != null)
            {
                ConnectionStatusLabel.Text = message;
                ConnectionStatusLabel.TextColor = (Color)Resources["ColorTextThird"];
            }
        });
    }


    // ========== ПОМЕНЯТЬ СТАТУС ТРЕВОГИ ==========
    private void UpdateAlarmStatus(bool isAlarm=false)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var animatedGradient = AnimatedGradient;


            if (AlarmStatusLabel != null)
            {
                AlarmStatusLabel.TextColor = (Color)Resources["ColorTextMain"];

                if (isAlarm)
                {
                    var themeColor = (Color)Resources["ColorLedRed"];
                    AlarmStatusLabel.Text = "⚠ ВНИМАНИЕ ⚠ \nТребуется очистка трубопровода"; 
                    AlarmStatusBorder.Stroke = Colors.Yellow;
                    AlarmStatusBorder.Background = themeColor;

                    AlarmThreshold.MinimumTrackColor = themeColor;
                    AlarmThreshold.ThumbColor = themeColor;

                    SensetivitySlider.MinimumTrackColor = themeColor;
                    SensetivitySlider.ThumbColor = themeColor;

                    // Красная рамка при тревоге
                    animatedGradient.GradientStops[0].Color = Colors.IndianRed;
                    animatedGradient.GradientStops[3].Color = Colors.IndianRed;
                }
                else
                {
                    var themeColor = Colors.DarkBlue;
                    AlarmStatusLabel.Text = "✔ Очистка завершена ✔\nТрубопровод готов к герметизации"; 
                    AlarmStatusBorder.Stroke = (Color)Resources["ColorTextThird"];
                    AlarmStatusBorder.Background = Colors.White;

                    AlarmThreshold.MinimumTrackColor = themeColor;
                    AlarmThreshold.ThumbColor = themeColor;

                    SensetivitySlider.MinimumTrackColor = themeColor;
                    SensetivitySlider.ThumbColor = themeColor;

                    // Обычная рамка при отсутствии тревоги
                    animatedGradient.GradientStops[0].Color = themeColor;
                    animatedGradient.GradientStops[3].Color = themeColor;
                }
            }
        });
    }


    // ========== СООБЩЕНИЕ, ЧТО УСТРОЙСТВО НЕ НАЙДЕНО ==========
    private void SendNotFound()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateConnectionStatus("Поиск устройства...");
            AddDataToUI("❗ Убедитесь, что устройство включено. Продолжаем поиск...🔎");
        });
    }


    // ===================================================
    // ========== ДОБАВЛЕНИЕ ДАННЫХ В КОНСОЛЬ ============
    // ===================================================
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
            while (DataContainer.Children.Count > 30)
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
        UpdateConnectionStatus("Ожидание Bluetooth...");
        while (true)
        {
            if (_bluetoothLE.State != BluetoothState.On) await Task.Delay(2000);
            else break;
        }

        // ✅ Проверяем разрешения и геолокацию для отладки
        //var permissionsOk = await EnsurePermissionsAndLocation();
        //if (!permissionsOk)
        //{
        //    AddDataToUI("❌ Недостаточно прав для BLE сканирования");
        //    UpdateConnectionStatus("Ошибка прав");
        //    return;
        //}

        UpdateConnectionStatus("Поиск устройства...");

        while (true)
        {
            if (_connectedDevice != null || _isScanning) break;
            _isScanning = true;

            // Настройка таймаута сканирования BL устройств
            _adapter.ScanTimeout = 3000;

            // Сканируем 3 секунды
            // Не ожидаем StartScanningForDevicesAsync, т.к. он блокирует интерфейс
            _ = _adapter.StartScanningForDevicesAsync();
            await Task.Delay(3000);
            await _adapter.StopScanningForDevicesAsync();

            // Ищем ESP32 среди найденных устройств
            var devices = _adapter.DiscoveredDevices;
            IDevice targetDevice = null!;

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
            _ = ConnectToDevice(targetDevice);
            _isScanning = false;
            break;
        }
    }


    // ========== ПОДКЛЮЧЕНИЕ К УСТРОЙСТВУ ==========
    private async Task ConnectToDevice(IDevice device)
    {
        if (_isConnecting) return;
        _isConnecting = true;

        try
        {
            UpdateConnectionStatus($"Подключение...");

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
                throw;
            }

            // Небольшая задержка для стабилизации
            await Task.Delay(500);

            // Получаем сервисы
            var services = await _connectedDevice.GetServicesAsync();

            // Ищем наш сервис по UUID
            var targetService = services.FirstOrDefault(s => s.Id == SERVICE_UUID);
            if (targetService == null) throw new Exception($"Сервис {SERVICE_UUID} не найден!");

            // Получаем характеристики
            var characteristics = await targetService.GetCharacteristicsAsync();

            // Ищем TX характеристику
            var txCharacteristic = characteristics.FirstOrDefault(c => c.Id == TX_CHARACTERISTIC_UUID);
            if (txCharacteristic == null) throw new Exception("TX характеристика не найдена!");

            // Ищем RX характеристику
            var rxCharacteristic = characteristics.FirstOrDefault(c => c.Id == RX_CHARACTERISTIC_UUID);
            if (rxCharacteristic == null) throw new Exception("RX характеристика не найдена!");

            _notifyCharacteristic = txCharacteristic;
            _writeCharacteristic = rxCharacteristic;
            _notifyCharacteristic.ValueUpdated += OnDataReceived;

            // Подписываемся на уведомления
            await _notifyCharacteristic.StartUpdatesAsync();

            _isConnecting = false;
        }
        catch (Exception ex)
        {
            UpdateConnectionStatus("Ошибка подключения");
            AddDataToUI($"❌ Ошибка: {ex.Message}");
            _ = DisconnectAndReset();
        }
    }


    // ===================================================
    // ========== ДЕЙТСВИЯ ПРИ ПОЛУЧЕНИИ ДАННЫХ ==========
    // ===================================================
    private void OnDataReceived(object sender, CharacteristicUpdatedEventArgs e)
    {
        // Обновляем время последнего получения данных для отслеживания таймаута
        _lastDataReceivedTime = DateTime.Now;
        _ = SyncWithServer();

        var bytes = e.Characteristic.Value;
        if (bytes != null && bytes.Length > 0)
        {
            var data = Encoding.UTF8.GetString(bytes);
            data = data.Trim().Replace("\0", "");

            if (!string.IsNullOrWhiteSpace(data))
            {
                List<string> ledsToReset = [
                        "Led1", "Led2", "Led3", "Led4", "Led5", "Led6",
                    ];
                if (data.Contains("Led"))
                {
                    string[] refinedData = data.Split("|");
                    // Создаём словарь
                    var dict = new Dictionary<string, string>();
                    foreach (string pair in refinedData)
                    {
                        string[] pairValues = pair.Split(':');
                        if (pairValues.Length > 1) dict[pairValues[0]] = pairValues[1];
                    }

                    string threshold = dict["Threshold"].Replace(".", ",");
                    string feedString = " ";
                    int feedStringLength = 0;

                    foreach (string key in dict.Keys)
                    {
                        // Ключи датчиков
                        if (key.Contains("Led"))
                        {
                            if (feedStringLength % 3 == 0) feedString += "\n";
                            feedStringLength++;

                            feedString += key.Replace("Led", "Датчик ") + ": " + dict[key].PadRight(10) + " ";
                            string lightLevel = dict[key].Replace(".", ",");
                            ledsToReset.Remove(key);
                            switch (key)
                            {
                                case "Led1":
                                    SwitchLed(Led1, lightLevel, threshold);
                                    break;
                                case "Led2":
                                    SwitchLed(Led2, lightLevel, threshold);
                                    break;
                                case "Led3":
                                    SwitchLed(Led3, lightLevel, threshold);
                                    break;
                                case "Led4":
                                    SwitchLed(Led4, lightLevel, threshold);
                                    break;
                                case "Led5":
                                    SwitchLed(Led5, lightLevel, threshold);
                                    break;
                                case "Led6":
                                    SwitchLed(Led6, lightLevel, threshold);
                                    break;
                                default:
                                    break;
                            }
                        }
                    }

                    //feedString += "Порог: " + dict["Threshold"] + "\n";
                    //feedString += "Тревога: " + (dict["IsAlarm"] == "true" ? "Да" : "Нет") + "\n";
                    //feedString += "Чувствительность: " + dict["MTreg"] + "\n";

                    if (dict["IsAlarm"] == "true") UpdateAlarmStatus(true);
                    else UpdateAlarmStatus();

                    AddDataToUI(feedString);
                }
                else AddDataToUI("\n" + data.Replace("|", "   "));

                foreach (string led in ledsToReset)
                {
                    ResetLed(led);
                }
            }
        }
    }


    private async void OnDeviceConnected(object sender, DeviceEventArgs args)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateConnectionStatus("Подключен");
            _ = SyncWithServer();

            // ✅ ЗАПУСКАЕМ МОНИТОРИНГ ТАЙМАУТА
            StartTimeoutMonitor();
            ResetGui();
        });
    }


    private async void OnDeviceDisconnected(object sender, DeviceEventArgs args)
    {
        MainThread.BeginInvokeOnMainThread(() => {});
    }


    // =============================================
    // ========== ПОПЫТКА ПЕРЕПОДКЛЮЧЕНИЯ ==========
    // =============================================
    private async Task TryReconnect()
    {
        while (true)
        {
            if (_connectedDevice != null)
            {
                break;
            }
            if (_bluetoothLE.State == BluetoothState.On)
            {
                UpdateConnectionStatus("Переподключение...");
                await AutoConnectToDevice();
            }
            await Task.Delay(5000);
        }
    }


    // ========== ОБРАБОТЧИК КНОПКИ ПЕРЕПОДКЛЮЧЕНИЯ ==========
    private async void OnReconnectClicked(object sender, EventArgs e)
    {
        AddDataToUI("🔄 Выполняется переподключение...");
        _ = DisconnectAndReset();    
    }


    // ========== ОБРАБОТЧИК КНОПКИ ПЕРЕПОДКЛЮЧЕНИЯ ==========
    private async void OnInfoClicked(object sender, EventArgs e)
    {
        _ = SendBLEData("?");    
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();

        // Останавливаем таймер
        _dataTimeoutTimer?.Dispose();
        _dataTimeoutTimer = null;

        if (_connectedDevice != null && _adapter != null)
        {
            if (_notifyCharacteristic != null)
            {
                _notifyCharacteristic.ValueUpdated -= OnDataReceived;
                try
                {
                    await _notifyCharacteristic.StopUpdatesAsync();
                }
                catch { /* Игнорируем */ }
            }

            try
            {
                await _adapter.DisconnectDeviceAsync(_connectedDevice);
            }
            catch { /* Игнорируем */ }
        }
    }

    
    // ========== ИЗМЕНЕНИЕ ЦВЕТА ДАТЧИКА ==========
    public void SetLedColorWithGradient(Border led, Color baseColor)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            int timeToDelay = GetRandomDelay();
            await Task.Delay(timeToDelay);

            // Создаем градиент
            var gradient = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };

            gradient.GradientStops.Add(new GradientStop(baseColor, 0.0f));
            gradient.GradientStops.Add(new GradientStop(baseColor.MultiplyAlpha(0.8f), 0.3f));
            gradient.GradientStops.Add(new GradientStop(baseColor.MultiplyAlpha(0.6f), 0.6f));
            gradient.GradientStops.Add(new GradientStop(baseColor.MultiplyAlpha(0.4f), 1.0f));

            led.Background = gradient;

            // Анимация тени (пульсация)
            var shadowAnimation = new Animation();
            shadowAnimation.Add(0, 1, new Animation(v =>
            {
                led.Shadow = new Shadow
                {
                    Brush = baseColor,
                    Radius = (float)(8 + v * 16),
                    Offset = new Point(0, 0),
                    Opacity = 1f
                };
            }, 0, 1, Easing.CubicInOut));

            shadowAnimation.Commit(led, "ShadowPulse", length: 300, repeat: () => false);
        });
    }


    // ========== ОБРАБОТЧИК ПОЛЗУНКА ЧУВСТВИТЕЛЬНОСТИ ==========
    private async void SensetivitySliderValueChanged(object sender, ValueChangedEventArgs e)
    {
        int newValue = (int)Math.Round(e.NewValue);
        int valueToSend;
        if (newValue == 0) valueToSend = 31;
        else valueToSend = 100;

        // Блокируем повторные вызовы во время отправки
        if (_isSendingCommand) return;
        _isSendingCommand = true;

        // Формируем команду: T + значение
        string command = $"T{valueToSend}";
        await SendBLEData(command);

        // Ставим значения вручную для избежания рассинхрона на серваке и клиенте
        //SensetivityValueLabel.Text = newValue.ToString();
        SensetivitySlider.Value = newValue;

        _isSendingCommand = false;
    }


    // ========== ОБРАБОТЧИК ПОЛЗУНКА ПОРОГА ==========
    private async void ThresholdValueChanged(object sender, ValueChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            int newValue = (int)Math.Round(e.NewValue);

            // Блокируем повторные вызовы во время отправки или подключения
            if (_isSendingCommand) return;
            _isSendingCommand = true;

            // Формируем команду: S + значение
            string command = $"S{newValue}";
            await SendBLEData(command);

            // Ставим значения вручную для избежания рассинхрона на серваке и клиенте
            ThresholdValueLabel.Text = newValue.ToString();
            AlarmThreshold.Value = newValue;

            _isSendingCommand = false;
        });
    }


    // ============================================
    // ========== ОТПРАВКА ДАННЫХ ПО BLE ==========
    // ============================================
    private async Task SendBLEData(string command)
    {
        if (_connectedDevice == null || _isScanning || _isConnecting) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(command);
            if (_writeCharacteristic != null) await _writeCharacteristic.WriteAsync(bytes);
            else throw new Exception();
        }
        catch (Exception)
        {
            AddDataToUI($"❌ Не удалось установить параметры.");
            if (_connectedDevice != null) _ = DisconnectAndReset();
        }
    }


    // ========== АНИМАЦИЯ РАМКИ ПРИЛОЖЕНИЯ ==========
    private async Task AnimateMainBorder()
    {
        double offset = 0;

        while (true)
        {
            offset += 0.01;

            // БОЛЬШЕ ЗНАЧЕНИЯ - БОЛЬШЕ ПЕРИОД АНИМАЦИИ
            // ОТ -1 ДО 1 МИНИМУМ, ЧТОБЫ НЕ БЫЛО РЫВКОВ
            if (offset > 1.0) offset = -1.0; 

            AnimatedGradient.StartPoint = new Point(offset, offset);
            AnimatedGradient.EndPoint = new Point(offset + 1, offset + 1);

            await Task.Delay(45);
        }
    }


    // ========== АНИМАЦИЯ РАМКИ СТАТУСА ТРЕВОГИ ==========
    private async Task AnimateAlarmBorder()
    {
        double strokeOffset = 0;

        while (true)
        {
            if (strokeOffset == 10.0) strokeOffset = 0.0;
            strokeOffset += 0.1;
            AlarmStatusBorder.StrokeDashOffset = strokeOffset;

            await Task.Delay(45);
        }
    }


    // ========== ЗАПУСК МОНИТОРИНГА ТАЙМАУТА ==========
    private void StartTimeoutMonitor()
    {
        _lastDataReceivedTime = DateTime.Now;

        // Запускаем таймер, который проверяет каждые 5 секунд
        _dataTimeoutTimer = new Timer(CheckTimeout, null, 100, 3000);
    }


    // ========== ПРОВЕРКА ТАЙМАУТА ==========
    private async void CheckTimeout(object state)
    {
        var timeSinceLastData = (DateTime.Now - _lastDataReceivedTime).TotalSeconds;

        if (timeSinceLastData > DATA_TIMEOUT_SECONDS && _connectedDevice != null)
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                UpdateConnectionStatus("Потеря соединения");
                AddDataToUI("🔄 Попытка автоматического переподключения...");
                await DisconnectAndReset();
            });
        }
    }


    // ========== СМЕНА ЦВЕТА ДАТЧИКОВ ==========
    private void SwitchLed(Border led, string lightlevel, string threshold)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (float.Parse(lightlevel) < float.Parse(threshold))
            {
                SetLedColorWithGradient(led, (Color)Resources["ColorLedBlue"]);
            }
            else SetLedColorWithGradient(led, (Color)Resources["ColorLedRed"]);
        });
    }


    // ========== CБРОС ЦВЕТА ДАТЧИКОВ ==========
    private void ResetLed(string led)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            int timeToDelay = GetRandomDelay();
            await Task.Delay(timeToDelay);

            var defaultGradient = new LinearGradientBrush();

            defaultGradient.StartPoint = new Point(0, 0);
            defaultGradient.EndPoint = new Point(1, 1);

            defaultGradient.GradientStops.Add(new GradientStop(Color.FromArgb("#B0B0B0"), 0.0f));
            defaultGradient.GradientStops.Add(new GradientStop(Color.FromArgb("#808080"), 0.4f));
            defaultGradient.GradientStops.Add(new GradientStop(Color.FromArgb("#555555"), 0.7f));
            defaultGradient.GradientStops.Add(new GradientStop(Color.FromArgb("#333333"), 1.0f));

            switch (led)
            {
                case "Led1":
                    Led1.Background = defaultGradient;
                    Led1.Shadow = null;
                    break;
                case "Led2":
                    Led2.Background = defaultGradient;
                    Led2.Shadow = null;
                    break;
                case "Led3":
                    Led3.Background = defaultGradient;
                    Led3.Shadow = null;
                    break;
                case "Led4":
                    Led4.Background = defaultGradient;
                    Led4.Shadow = null;
                    break;
                case "Led5":
                    Led5.Background = defaultGradient;
                    Led5.Shadow = null;
                    break;
                case "Led6":
                    Led6.Background = defaultGradient;
                    Led6.Shadow = null;
                    break;
                default:
                    break;
            }
        });
    }


    // ========== CБРОС ЦВЕТА ДАТЧИКОВ ==========
    private void ResetAllLeds()
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var defaultGradient = new LinearGradientBrush();

            defaultGradient.StartPoint = new Point(0, 0);
            defaultGradient.EndPoint = new Point(1, 1);

            defaultGradient.GradientStops.Add(new GradientStop(Color.FromArgb("#B0B0B0"), 0.0f));
            defaultGradient.GradientStops.Add(new GradientStop(Color.FromArgb("#808080"), 0.4f));
            defaultGradient.GradientStops.Add(new GradientStop(Color.FromArgb("#555555"), 0.7f));
            defaultGradient.GradientStops.Add(new GradientStop(Color.FromArgb("#333333"), 1.0f));

            List<Border> ledsToReset = [
                Led1, Led2, Led3, Led4, Led5, Led6
            ];

            foreach (Border led in ledsToReset)
            {
                led.Background = defaultGradient;
                led.Shadow = null;
            }

            //ResetLed("Led1");
            //ResetLed("Led2");
            //ResetLed("Led3");
            //ResetLed("Led4");
            //ResetLed("Led5");
            //ResetLed("Led6");
        });
    }


    // ========== ВОЗВРАТ ИНТЕРФЕЙСА К ИСХОДНОМУ ПОЛОЖЕНИЮ ==========
    private void ResetGui()
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            ResetAllLeds();

            AlarmStatusBorder.Stroke = Colors.White;
            AlarmStatusBorder.Background = Colors.White;
            AlarmStatusLabel.Text = " \n ";

            //// ✅ Временно отключаем обработчики событий
            //AlarmThreshold.ValueChanged -= ThresholdValueChanged;
            //Sensetivity.ValueChanged -= SensetivityValueChanged;

            //// Устанавливаем значения
            //AlarmThreshold.Value = 10;
            //ThresholdValueLabel.Text = "10";
            //Sensetivity.Value = 31;
            //SensetivityValueLabel.Text = "31";

            //// ✅ Возвращаем обработчики обратно
            //AlarmThreshold.ValueChanged += ThresholdValueChanged;
            //Sensetivity.ValueChanged += SensetivityValueChanged;

            AnimatedGradient.GradientStops[0].Color = Colors.DarkBlue;
            AnimatedGradient.GradientStops[3].Color = Colors.DarkBlue;

            AlarmThreshold.MinimumTrackColor = Colors.DarkBlue;
            AlarmThreshold.ThumbColor = Colors.DarkBlue;

            SensetivitySlider.MinimumTrackColor = Colors.DarkBlue;
            SensetivitySlider.ThumbColor = Colors.DarkBlue;
        });
    }

    private async Task SyncWithServer()
    {
        int thresholdValue = (int)Math.Round(AlarmThreshold.Value);
        string command = $"S{thresholdValue}";
        await SendBLEData(command);

        int sensetivityValue = (int)Math.Round(SensetivitySlider.Value);
        if (sensetivityValue == 0) command = $"T{31}";
        else command = $"T{100}";
        await SendBLEData(command);

    }

    // ========== ПОЛУЧЕНИЕ РАНДОМНОЙ ЗАДЕРЖКИ ==========
    private int GetRandomDelay()
    {
        Random random = new Random();
        int delay = random.Next(0, 500);

        return delay;
    }
}