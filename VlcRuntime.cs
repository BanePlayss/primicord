using System.IO.Compression;
using System.Reflection;

namespace Primicord;

/// <summary>
/// Instala o motor de video (libVLC) na primeira vez que a sessao cinema e usada.
/// </summary>
/// <remarks>
/// POR QUE ISTO EXISTE: o libVLC nao e uma DLL solta — sao ~100MB entre libvlc.dll,
/// libvlccore.dll e uma pasta de plugins com centenas de arquivos. Com o publish em
/// arquivo unico, essa pasta simplesmente NAO ia junto (so os .lib inuteis eram
/// copiados), e a sessao cinema abria sem tocar nada.
///
/// Solucao: o build empacota uma versao enxuta do VLC num zip que vai EMBUTIDO no
/// exe, e na primeira sessao ele se extrai pra %APPDATA%\Primicord\vlc. Continua
/// sendo um arquivo so pra mandar pros amigos, e o cinema funciona sem instalar nada.
/// </remarks>
public static class VlcRuntime
{
    private const string ResourceName = "vlc-runtime.zip";

    public static string InstallDir => Path.Combine(AppEnv.DataDir, "vlc");

    /// <summary>true se o motor ja esta instalado e pronto.</summary>
    public static bool Installed => File.Exists(Path.Combine(InstallDir, "libvlc.dll"))
                                 && Directory.Exists(Path.Combine(InstallDir, "plugins"));

    /// <summary>true se este build tem o pacote (em arquivo ou embutido).</summary>
    public static bool HasEmbeddedPackage => PackageFile() != null || FindResource() != null;

    /// <summary>
    /// O zip como ARQUIVO ao lado do exe — o formato preferido desde que o app passou
    /// a ser instalado.
    /// </summary>
    /// <remarks>
    /// Embutido no assembly, estes 38,7MB entravam no arquivo que muda a cada versao,
    /// e a atualizacao diferencial precisaria baixar tudo de novo por causa de uma
    /// linha de codigo. Como arquivo solto ele fica identico entre versoes, entao o
    /// delta simplesmente nao o inclui. O caminho embutido continua existindo pra
    /// nao quebrar quem estiver num exe antigo de arquivo unico.
    /// </remarks>
    private static string? PackageFile()
    {
        try
        {
            string? dir = Path.GetDirectoryName(Environment.ProcessPath ?? "");
            if (string.IsNullOrEmpty(dir)) return null;
            string p = Path.Combine(dir, ResourceName);
            return File.Exists(p) ? p : null;
        }
        catch { return null; }
    }

    private static string? FindResource()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (string n in asm.GetManifestResourceNames())
            if (n.EndsWith(ResourceName, StringComparison.OrdinalIgnoreCase)) return n;
        return null;
    }

    /// <summary>Abre o pacote, venha ele de onde vier.</summary>
    private static Stream? OpenPackage()
    {
        string? file = PackageFile();
        if (file != null) return File.OpenRead(file);
        string? res = FindResource();
        return res == null ? null : Assembly.GetExecutingAssembly().GetManifestResourceStream(res);
    }

    /// <summary>
    /// Garante o motor instalado. Devolve a pasta, ou null se nao deu.
    /// <paramref name="progress"/> recebe o percentual da extracao.
    /// </summary>
    public static string? Ensure(Action<int>? progress = null)
    {
        try
        {
            if (Installed) return InstallDir;

            var pacote = OpenPackage();
            if (pacote == null)
            {
                Log.Write("VLC: pacote ausente neste build");
                return null;
            }

            Log.Write("VLC: instalando em " + InstallDir);
            // Extrai pra uma pasta temporaria e so depois renomeia: se travar no meio,
            // nao fica um diretorio meio-instalado que parece pronto.
            string staging = InstallDir + ".tmp";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);

            using (pacote)
            using (var zip = new ZipArchive(pacote, ZipArchiveMode.Read))
            {
                int total = zip.Entries.Count, done = 0, lastPct = -1;
                foreach (var entry in zip.Entries)
                {
                    string dest = Path.Combine(staging, entry.FullName);
                    string? dir = Path.GetDirectoryName(dest);
                    if (dir != null) Directory.CreateDirectory(dir);
                    if (entry.Name.Length > 0) entry.ExtractToFile(dest, true);

                    done++;
                    int pct = done * 100 / Math.Max(1, total);
                    if (pct != lastPct) { lastPct = pct; progress?.Invoke(pct); }
                }
            }

            if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, true);
            Directory.Move(staging, InstallDir);
            Log.Write("VLC: instalado");
            return Installed ? InstallDir : null;
        }
        catch (Exception ex)
        {
            Log.Write("VLC: instalacao falhou: " + ex.Message);
            return null;
        }
    }
}
