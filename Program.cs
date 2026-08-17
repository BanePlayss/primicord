using Velopack;

namespace Primicord;

internal static class Program
{
    /// <summary>Impede duas instancias (duas sessoes brigariam pelo microfone).</summary>
    private static Mutex? _single;

    [STAThread]
    private static void Main(string[] args)
    {
        // PRIMEIRA coisa do processo, sem excecao. Em instalacao, atualizacao e
        // desinstalacao o Velopack faz o trabalho dele aqui e ENCERRA o processo —
        // qualquer coisa antes disto (mutex, janela, log) roda a toa ou atrapalha.
        VelopackApp.Build().Run();

        // --updated: fomos abertos pelo atualizador e o processo velho ainda esta
        // morrendo. Sem isso a versao nova bateria no "ja esta aberto" e sairia,
        // deixando o usuario sem app nenhum na tela depois de atualizar.
        bool afterUpdate = args.Contains("--updated", StringComparer.OrdinalIgnoreCase);

        bool created = TryTakeMutex();
        if (!created && afterUpdate)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!created && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(250);
                created = TryTakeMutex();
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

        Log.Write($"=== Primicord {Updater.CurrentVersion} iniciando ===");
        try { Application.Run(new MainForm()); }
        finally
        {
            Log.Write("=== Primicord encerrado ===");
            try { _single?.ReleaseMutex(); } catch { }
        }
    }

    private static bool TryTakeMutex()
    {
        try
        {
            _single?.Dispose();
            _single = new Mutex(true, "Primicord.SingleInstance", out bool created);
            if (!created) { _single.Dispose(); _single = null; }
            return created;
        }
        catch { return true; }   // sem mutex e melhor que sem app
    }
}
