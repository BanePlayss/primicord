namespace Primicord;

internal static class Program
{
    /// <summary>Impede duas instancias (duas sessoes brigariam pelo microfone).</summary>
    private static Mutex? _single;

    [STAThread]
    private static void Main()
    {
        bool created;
        try { _single = new Mutex(true, "Primicord.SingleInstance", out created); }
        catch { created = true; }

        if (!created)
        {
            MessageBox.Show("O Primicord ja esta aberto.", "PRIMICORD",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Write("EXCECAO NAO TRATADA: " + (e.ExceptionObject as Exception)?.ToString());
        Application.ThreadException += (_, e) =>
            Log.Write("EXCECAO NA UI: " + e.Exception);

        Log.Write("=== Primicord iniciando ===");
        try { Application.Run(new MainForm()); }
        finally
        {
            Log.Write("=== Primicord encerrado ===");
            try { _single?.ReleaseMutex(); } catch { }
        }
    }
}
