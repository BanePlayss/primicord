namespace Primicord;

internal static class Program
{
    /// <summary>Impede duas instancias (duas sessoes brigariam pelo microfone).</summary>
    private static Mutex? _single;

    [STAThread]
    private static void Main(string[] args)
    {
        bool preview = args.Any(a => string.Equals(a, "--preview", StringComparison.OrdinalIgnoreCase));
        bool created;
        string mutexName = preview ? "Primicord.Preview." + Environment.ProcessId : "Primicord.SingleInstance";
        try { _single = new Mutex(true, mutexName, out created); }
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
        try
        {
            var form = new MainForm(preview);
            int render = Array.IndexOf(args, "--render-preview");
            if (preview && render >= 0 && render + 1 < args.Length)
                form.Shown += (_,_) => form.BeginInvoke(() => form.RenderPreviewChecks(Path.GetFullPath(args[render + 1])));
            Application.Run(form);
        }
        finally
        {
            Log.Write("=== Primicord encerrado ===");
            try { _single?.ReleaseMutex(); } catch { }
        }
    }
}
