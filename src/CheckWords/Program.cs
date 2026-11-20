namespace CheckWords;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, @"Local\CheckWords.SingleInstance", out bool first);
        if (!first) return;

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.Message, "CheckWords", MessageBoxButtons.OK, MessageBoxIcon.Error);

        using var app = new TrayApp();
        Application.Run(app);
    }
}
