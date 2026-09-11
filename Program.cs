using Velopack;

namespace Primicord;

internal static class Program
{
    /// <summary>Impede duas instancias (duas sessoes brigariam pelo microfone).</summary>
    private static Mutex? _single;

    [STAThread]
    private static void Main(string[] args)
    {
        // Precisa ser a primeira chamada: o Velopack trata install/update/uninstall
        // antes de abrir a janela normal.
        VelopackApp.Build().Run();
        bool preview = args.Any(a => string.Equals(a, "--preview", StringComparison.OrdinalIgnoreCase));
        bool afterUpdate = args.Any(a => string.Equals(a, "--updated", StringComparison.OrdinalIgnoreCase));
        string mutexName = preview ? "Primicord.Preview." + Environment.ProcessId : "Primicord.SingleInstance";
        bool created = TryTakeMutex(mutexName);
        if (!created && afterUpdate)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!created && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(250);
                created = TryTakeMutex(mutexName);
            }
        }

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
            UiMotion.Reduced = args.Contains("--render-preview");
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

    private static bool TryTakeMutex(string name)
    {
        try
        {
            _single?.Dispose();
            _single = new Mutex(true, name, out bool created);
            if (!created) { _single.Dispose(); _single = null; }
            return created;
        }
        catch { return true; }
    }
}
