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

    private bool _isConnecting = false;
    private bool _isSendingCommand = false;

    // Для отслеживания таймаута данных
    private DateTime _lastDataReceivedTime;
    private Timer _dataTimeoutTimer;
    private const int DATA_TIMEOUT_SECONDS = 10;


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
        AnimateMainBorder();
        InitializeBluetooth();

#if ANDROID
        Loaded += async (s, e) => await CheckAndRequestPermissions();
        //CheckAndRequestPermissions();
#endif

        // Автоматически начинаем поиск и подключение при запуске
        Loaded += async (s, e) => await AutoConnectToDevice();
    }

    public void Dispose()
    {
        // Принудительная чистка
        _dataTimeoutTimer?.Dispose();
        _notifyCharacteristic?.StopUpdatesAsync().ConfigureAwait(false);
        _adapter?.DisconnectDeviceAsync(_connectedDevice).ConfigureAwait(false);
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
                    AlarmStatusLabel.Text = "⚠ ВНИМАНИЕ ⚠ \nТребуется очистка трубопровода"; 
                    AlarmStatusBorder.Stroke = Colors.Yellow;
                    AlarmStatusBorder.Background = (Color)Resources["ColorLedRed"];

                    // Красная рамка при тревоге
                    animatedGradient.GradientStops[0].Color = Colors.IndianRed;
                    animatedGradient.GradientStops[3].Color = Colors.IndianRed;
                }
                else
                {
                    AlarmStatusLabel.Text = "✔ Очистка завершена ✔\nТрубопровод готов к герметизации"; 
                    AlarmStatusBorder.Stroke = (Color)Resources["ColorTextThird"];
                    //AlarmStatusBorder.Background = (Color)Resources["ColorLedBlue"];
                    AlarmStatusBorder.Background = Colors.White;

                    // Обычная рамка при отсутствии тревоги
                    animatedGradient.GradientStops[0].Color = Colors.DarkBlue;
                    animatedGradient.GradientStops[3].Color = Colors.DarkBlue;
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
        // Ждем включения Bluetooth
        UpdateConnectionStatus("Ожидание Bluetooth...");
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

        // ✅ Проверяем разрешения и геолокацию для отладки
        //var permissionsOk = await EnsurePermissionsAndLocation();
        //if (!permissionsOk)
        //{
        //    AddDataToUI("❌ Недостаточно прав для BLE сканирования");
        //    UpdateConnectionStatus("Ошибка прав");
        //    return;
        //}

        UpdateConnectionStatus("Поиск устройства...");


        // Создаем фильтр для сканирования по Service UUID
        var scanFilterOptions = new ScanFilterOptions
        {
            DeviceNames = ["ESP32_Oil_Detector_DonskoyPN"],
        };

        while (true)
        {
            // Настройка таймаута сканирования BL устройств
            _adapter.ScanTimeout = 3000;

            // Сканируем 3 секунды
            await _adapter.StartScanningForDevicesAsync();
            //await Task.Delay(3000);
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
                await TryReconnect();
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
                await TryReconnect();
                return;
            }

            // Получаем характеристики
            var characteristics = await targetService.GetCharacteristicsAsync();

            // Ищем TX характеристику
            var txCharacteristic = characteristics.FirstOrDefault(c => c.Id == TX_CHARACTERISTIC_UUID);

            if (txCharacteristic == null)
            {
                AddDataToUI("❌ TX характеристика не найдена!");
                await TryReconnect();
                return;
            }

            // Ищем RX характеристику
            var rxCharacteristic = characteristics.FirstOrDefault(c => c.Id == RX_CHARACTERISTIC_UUID);

            if (rxCharacteristic == null)
            {
                AddDataToUI("❌ RX характеристика не найдена!");
                await TryReconnect();
                return;
            }

            _notifyCharacteristic = txCharacteristic;
            _writeCharacteristic = rxCharacteristic;
            _notifyCharacteristic.ValueUpdated += OnDataReceived;

            // Подписываемся на уведомления
            await _notifyCharacteristic.StartUpdatesAsync();

            _isConnecting = false;

            // ✅ ЗАПУСКАЕМ МОНИТОРИНГ ТАЙМАУТА
            StartTimeoutMonitor();
        }
        catch (Exception ex)
        {
            UpdateConnectionStatus("Ошибка подключения");
            AddDataToUI($"❌ Ошибка: {ex.Message}");

            if (_notifyCharacteristic != null)
            {
                _notifyCharacteristic.ValueUpdated -= OnDataReceived;
                _notifyCharacteristic = null;
            }

            _isConnecting = false;
            _connectedDevice = null;
            await TryReconnect();
        }
    }


    // ===================================================
    // ========== ДЕЙТСВИЯ ПРИ ПОЛУЧЕНИИ ДАННЫХ ==========
    // ===================================================
    private void OnDataReceived(object sender, CharacteristicUpdatedEventArgs e)
    {
        // Обновляем время последнего получения данных для отслеживания таймаута
        _lastDataReceivedTime = DateTime.Now;

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

                    foreach (string key in dict.Keys)
                    {
                        // Ключи датчиков
                        if (key.Contains("Led"))
                        {
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
                else AddDataToUI(data.Replace("|", "   "));

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
        });
    }


    private async void OnDeviceDisconnected(object sender, DeviceEventArgs args)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateConnectionStatus("Потеря соединения");
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


    // =============================================
    // ========== ПОПЫТКА ПЕРЕПОДКЛЮЧЕНИЯ ==========
    // =============================================
    private async Task TryReconnect()
    {
        UpdateConnectionStatus("Потеря соединения");

        // Останавливаем таймер таймаута
        _dataTimeoutTimer?.Dispose();
        _dataTimeoutTimer = null;

        _isConnecting = false;
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
                UpdateConnectionStatus("Переподключение...");
                await AutoConnectToDevice();
            }
        }
    }


    // ========== ОБРАБОТЧИК КНОПКИ ПЕРЕПОДКЛЮЧЕНИЯ ==========
    private async void OnReconnectClicked(object sender, EventArgs e)
    {
        // Отключаем кнопку, чтобы избежать повторных нажатий
        var button = (Button)sender;
        button.IsEnabled = false;

        try
        {
            await TryReconnect();
        }
        finally
        {
            button.IsEnabled = true;
        }
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

            // Ставим значения вручную для избежания рассинхрона на серваке и клиенте
            SensetivityValueLabel.Text = newValue.ToString();
            Sensetivity.Value = newValue;
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

            // Ставим значения вручную для избежания рассинхрона на серваке и клиенте
            ThresholdValueLabel.Text = newValue.ToString();
            AlarmThreshold.Value = newValue;
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


    // ============================================
    // ========== ОТПРАВКА ДАННЫХ ПО BLE ==========
    // ============================================
    private async Task SendBLEData(string data)
    {
        if (_writeCharacteristic == null)
        {
            AddDataToUI("❌ BLE характеристика не инициализирована");
            await TryReconnect();
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
            TryReconnect();
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


    // ========== ЗАПУСК МОНИТОРИНГА ТАЙМАУТА ==========
    private void StartTimeoutMonitor()
    {
        _lastDataReceivedTime = DateTime.Now;

        // Запускаем таймер, который проверяет каждые 5 секунд
        _dataTimeoutTimer = new Timer(CheckTimeout, null, 5000, 5000);
    }


    // ========== ЗАПУСК МОНИТОРИНГА ТАЙМАУТА ==========
    private async void CheckTimeout(object state)
    {
        var timeSinceLastData = (DateTime.Now - _lastDataReceivedTime).TotalSeconds;

        if (timeSinceLastData > DATA_TIMEOUT_SECONDS && _connectedDevice != null)
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                UpdateConnectionStatus("Потеря соединения");
                AddDataToUI("Попытка переподключения...");
                await TryReconnect();
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


    // ========== ВОЗВРАТ ИНТЕРФЕЙСА К ИСХОДНОМУ ПОЛОЖЕНИЮ ==========
    private void ResetGui()
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {

        });
    }
}