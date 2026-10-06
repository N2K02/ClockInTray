namespace ClockInTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        bool restart;
        using (var mutex = new Mutex(true, @"Local\ClockInTray.SingleInstance", out var isFirst))
        {
            if (!isFirst)
            {
                return;
            }

            ApplicationConfiguration.Initialize();
            var context = new TrayApplicationContext();
            Application.Run(context);
            restart = context.RestartRequested;
        }

        // Start the new instance only after the single-instance mutex is released.
        if (restart && Environment.ProcessPath is { } exe)
        {
            System.Diagnostics.Process.Start(exe);
        }
    }
}
