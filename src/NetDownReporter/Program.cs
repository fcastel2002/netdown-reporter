namespace NetDownReporter;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var single = new Mutex(true, @"Local\NetDownReporter", out var first);
        if (!first) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
