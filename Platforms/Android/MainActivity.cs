//using Android.App;
//using Android.Content.PM;
//using Android.OS;

//namespace OilDetectorApp
//{
//    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
//    public class MainActivity : MauiAppCompatActivity
//    {
//    }
//}
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;

namespace OilDetectorApp;

[Activity(Theme = "@style/Maui.SplashTheme",
          MainLauncher = true,
          ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode,
          ScreenOrientation = ScreenOrientation.Portrait)]  // ✅ Добавлено
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // ✅ Дополнительная блокировка через код
        RequestedOrientation = ScreenOrientation.Portrait;
    }

    // ✅ Переопределяем метод для полной блокировки
    public override void OnConfigurationChanged(Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);

        // Принудительно возвращаем портретный режим при любом изменении
        if (RequestedOrientation != ScreenOrientation.Portrait)
        {
            RequestedOrientation = ScreenOrientation.Portrait;
        }
    }
}
