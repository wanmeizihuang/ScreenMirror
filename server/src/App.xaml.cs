using System.Windows;

namespace ScreenMirror.Server;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局异常处理
        DispatcherUnhandledException += (s, args) =>
        {
            MessageBox.Show(
                $"发生未处理异常:\n{args.Exception.Message}",
                "ScreenMirror 错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            MessageBox.Show(
                $"严重错误:\n{ex?.Message}",
                "ScreenMirror 崩溃",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        };
    }
}
