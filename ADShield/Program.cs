using ADShield.Forms;

namespace ADShield;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.ThreadException += (_, e) =>
        {
            MessageBox.Show(
                $"An unhandled error occurred:\n\n{e.Exception.Message}\n\n{e.Exception.StackTrace}",
                "AD Shield — Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            MessageBox.Show(
                $"A fatal error occurred:\n\n{ex?.Message}\n\n{ex?.StackTrace}",
                "AD Shield — Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        Application.Run(new MainForm());
    }
}
