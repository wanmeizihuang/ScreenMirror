using System.Windows;

namespace ScreenMirror.Server;

public partial class App : Application
{
    private static readonly object ErrorLogLock = new();
    private static readonly string ErrorLogPath = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "screenmirror-ui-error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局异常处理
        DispatcherUnhandledException += (s, args) =>
        {
            WriteError("UI", args.Exception);
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
            WriteError("FATAL", ex);
            MessageBox.Show(
                $"严重错误:\n{ex?.Message}",
                "ScreenMirror 崩溃",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        };
    }

    private static void WriteError(string category, Exception? exception)
    {
        try
        {
            lock (ErrorLogLock)
            {
                System.IO.File.AppendAllText(
                    ErrorLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {category}: {exception}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
