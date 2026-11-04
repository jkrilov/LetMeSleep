namespace LetMeSleep;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var instance = new Mutex(true, @"Global\LetMeSleep.Desktop", out var created);
        if (!created)
        {
            MessageBox.Show("LetMeSleep is already running.", "LetMeSleep");
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.Message, "LetMeSleep error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LetMeSleep", "overrides.json");
        Application.Run(new MainForm(new Powercfg(), new OverrideJournal(path)));
    }
}
