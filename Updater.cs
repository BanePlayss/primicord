using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Primicord;

/// <summary>Uma versao publicada la no GitHub, pronta pra baixar.</summary>
public sealed class UpdateInfo
{
    public string Version = "";
    public string Url = "";
    public long Size;
    public string Notes = "";
    public string Sha256 = "";   // vazio se a release nao publicou o hash

    public string SizeLabel => Size <= 0 ? "" : $"{Size / (1024.0 * 1024.0):0.0} MB";
}

/// <summary>
/// Atualizacao pelo proprio app: pergunta ao GitHub se tem versao nova, baixa e
/// se troca no lugar.
/// </summary>
/// <remarks>
/// POR QUE GITHUB RELEASES: nao precisa de servidor nenhum e nao custa nada, que
/// e a mesma regra do resto do projeto. Publicar uma versao vira um comando; o app
/// so le a API publica de releases. Nada de infraestrutura nova pra manter.
///
/// COMO SE TROCA UM EXE QUE ESTA RODANDO: o Windows nao deixa APAGAR nem
/// SOBRESCREVER o arquivo de um processo vivo — mas deixa RENOMEAR. Entao o
/// caminho e: renomeia o atual pra .old, poe o novo no lugar, sobe o novo e sai.
/// O .old fica pra tras e o proximo boot apaga (ai ninguem esta usando).
/// Isso evita ter que largar um .bat no disco pra fazer a troca depois, que e a
/// solucao classica e a que mais quebra (antivirus, politica de execucao, o
/// arquivo ficando pra tras quando algo falha no meio).
///
/// A CORRIDA DO MUTEX: o Program usa um mutex pra impedir duas instancias. Na
/// troca, o processo novo sobe enquanto o velho ainda esta morrendo, e ele bateria
/// direto no "o Primicord ja esta aberto". Por isso o novo sobe com --updated, que
/// manda ele INSISTIR no mutex por alguns segundos em vez de desistir de cara.
///
/// CONFIANCA: o binario nao e assinado. Quem garante a origem e o HTTPS ate o
/// GitHub — ou seja, a confianca esta em quem tem acesso de publicar no
/// repositorio, nao no arquivo em si. Um hash publicado junto NAO mudaria isso
/// (viria da mesma fonte); ele serve pra pegar download corrompido, e e so pra
/// isso que e usado aqui. Assinatura de codigo de verdade e outra fase.
/// </remarks>
public static class Updater
{
    /// <summary>
    /// Onde procurar as versoes, no formato dono/repositorio. Da pra trocar sem
    /// recompilar pela chave `updaterepo` do config.txt.
    /// </summary>
    public const string DefaultRepo = "BanePlayss/primicord";

    private const string AssetName = "Primicord.exe";

    /// <summary>Versao deste exe, vinda do &lt;Version&gt; do csproj.</summary>
    public static string CurrentVersion
    {
        get
        {
            try
            {
                var asm = Assembly.GetEntryAssembly() ?? typeof(Updater).Assembly;
                string? v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                               ?.InformationalVersion;
                // O SDK cola "+<hash do commit>" quando o repo e git.
                if (!string.IsNullOrEmpty(v))
                {
                    int plus = v.IndexOf('+');
                    return plus > 0 ? v[..plus] : v;
                }
                return asm.GetName().Version?.ToString(3) ?? "0.0.0";
            }
            catch { return "0.0.0"; }
        }
    }

    /// <summary>
    /// false quando rodando por `dotnet run`: nao ha um Primicord.exe pra trocar,
    /// e mexer no apphost de desenvolvimento so quebraria o ambiente.
    /// </summary>
    public static bool CanSelfUpdate
    {
        get
        {
            string? exe = Environment.ProcessPath;
            return exe != null &&
                   Path.GetFileName(exe).Equals(AssetName, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // Sem User-Agent a API do GitHub responde 403 direto.
        http.DefaultRequestHeaders.Add("User-Agent", "Primicord/" + CurrentVersion);
        http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        return http;
    }

    /// <summary>
    /// Pergunta qual e a versao mais recente. Devolve null quando ja estamos nela
    /// (ou quando a de la e mais velha).
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(string repo, CancellationToken ct = default)
    {
        using var http = NewClient();
        string url = $"https://api.github.com/repos/{repo}/releases/latest";

        using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException(
                $"O repositorio {repo} nao tem nenhuma versao publicada (ou nao e publico).");
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"O GitHub respondeu {(int)resp.StatusCode}.");

        var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        string tag = node?["tag_name"]?.GetValue<string>() ?? "";
        string notes = node?["body"]?.GetValue<string>() ?? "";

        var info = new UpdateInfo
        {
            Version = tag.TrimStart('v', 'V'),
            Notes = notes,
        };

        foreach (var a in node?["assets"]?.AsArray() ?? new JsonArray())
        {
            string name = a?["name"]?.GetValue<string>() ?? "";
            if (name.Equals(AssetName, StringComparison.OrdinalIgnoreCase))
            {
                info.Url = a?["browser_download_url"]?.GetValue<string>() ?? "";
                info.Size = a?["size"]?.GetValue<long>() ?? 0;
            }
            else if (name.Equals(AssetName + ".sha256", StringComparison.OrdinalIgnoreCase))
            {
                string? shaUrl = a?["browser_download_url"]?.GetValue<string>();
                if (shaUrl != null)
                {
                    try
                    {
                        string txt = await http.GetStringAsync(shaUrl, ct).ConfigureAwait(false);
                        // Aceita tanto "<hash>" quanto o formato do sha256sum: "<hash>  arquivo"
                        info.Sha256 = txt.Trim().Split(' ', '\t')[0].Trim();
                    }
                    catch (Exception ex) { Log.Write("update: hash nao veio: " + ex.Message); }
                }
            }
        }

        if (info.Url.Length == 0)
            throw new InvalidOperationException(
                $"A versao {tag} nao tem o arquivo {AssetName} anexado.");

        if (!IsNewer(info.Version, CurrentVersion))
        {
            Log.Write($"update: {CurrentVersion} ja e a mais recente (la esta {info.Version})");
            return null;
        }

        Log.Write($"update: {CurrentVersion} -> {info.Version} disponivel ({info.SizeLabel})");
        return info;
    }

    /// <summary>Compara duas versoes tipo "0.2.1". Texto invalido conta como antigo.</summary>
    public static bool IsNewer(string candidate, string current)
    {
        static Version Parse(string s)
        {
            var parts = s.Split('.', '-')
                         .TakeWhile(p => p.Length > 0 && p.All(char.IsDigit))
                         .Take(4).ToList();
            while (parts.Count < 2) parts.Add("0");
            return Version.TryParse(string.Join('.', parts), out var v) ? v : new Version(0, 0);
        }
        return Parse(candidate) > Parse(current);
    }

    /// <summary>
    /// Baixa pra %APPDATA%\Primicord\update e devolve o caminho. Confere o que da
    /// pra conferir antes de deixar o arquivo virar o app da proxima vez.
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<int>? progress,
                                                   CancellationToken ct = default)
    {
        string dir = Path.Combine(AppEnv.DataDir, "update");
        Directory.CreateDirectory(dir);
        string dest = Path.Combine(dir, AssetName);
        try { File.Delete(dest); } catch { }

        using var http = NewClient();
        using (var resp = await http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct)
                                    .ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? info.Size;
            long done = 0;

            using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var dst = File.Create(dest);
            var buf = new byte[128 * 1024];
            int n;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (total > 0) progress?.Report((int)(done * 100 / total));
            }
        }

        Verify(dest, info);
        Log.Write($"update: baixado e conferido em {dest}");
        return dest;
    }

    /// <summary>
    /// Barreira antes da troca. O que importa aqui nao e ataque — e download pela
    /// metade ou uma pagina de erro salva como se fosse o exe: sem estas contas, o
    /// app se substituiria por lixo e nao abriria mais.
    /// </summary>
    private static void Verify(string path, UpdateInfo info)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists || fi.Length < 1024 * 1024)
            throw new InvalidOperationException("O arquivo baixado veio pequeno demais pra ser o app.");

        if (info.Size > 0 && fi.Length != info.Size)
            throw new InvalidOperationException(
                $"O arquivo veio com {fi.Length} bytes, mas a versao anuncia {info.Size}.");

        // Todo .exe do Windows comeca com "MZ". Pega HTML de erro salvo por engano.
        using (var fs = File.OpenRead(path))
        {
            if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z')
                throw new InvalidOperationException("O arquivo baixado nao e um executavel.");
        }

        if (info.Sha256.Length > 0)
        {
            using var fs = File.OpenRead(path);
            string got = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            if (!got.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O hash do arquivo baixado nao bate.");
        }
    }

    /// <summary>
    /// Poe o novo exe no lugar do atual e sobe ele. Quem chama deve encerrar o app
    /// logo em seguida — os dois processos ficam vivos por um instante.
    /// </summary>
    public static void ApplyAndRestart(string downloadedExe)
    {
        string current = Environment.ProcessPath
            ?? throw new InvalidOperationException("Nao consegui descobrir o caminho do proprio exe.");
        string old = current + ".old";

        try { File.Delete(old); } catch { }

        // Renomear o proprio exe rodando E permitido; apagar ou sobrescrever nao.
        File.Move(current, old);
        try
        {
            File.Move(downloadedExe, current);
        }
        catch
        {
            // Falhou no meio: devolve o antigo pro lugar, senao o app some do disco.
            try { File.Move(old, current); } catch { }
            throw;
        }

        Process.Start(new ProcessStartInfo(current, "--updated") { UseShellExecute = true });
        Log.Write("update: novo exe no lugar, subindo a versao nova");
    }

    /// <summary>
    /// Apaga o exe da versao anterior. Chamado no boot, que e quando ninguem esta
    /// mais usando aquele arquivo.
    /// </summary>
    public static void CleanupOldVersion()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe == null) return;
            string old = exe + ".old";
            if (File.Exists(old)) { File.Delete(old); Log.Write("update: versao antiga apagada"); }
        }
        catch (Exception ex) { Log.Write("update: nao consegui apagar a versao antiga: " + ex.Message); }
    }
}
