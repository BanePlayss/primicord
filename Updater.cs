using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace Primicord;

/// <summary>Uma versao nova esperando pra ser instalada.</summary>
public sealed class UpdateInfoView
{
    public string Version = "";
    public long Size;
    public string Notes = "";

    /// <summary>true quando so o que mudou sera baixado, e nao o pacote inteiro.</summary>
    public bool IsDelta;

    public string SizeLabel => Size <= 0 ? "" : $"{Size / (1024.0 * 1024.0):0.0} MB";
}

/// <summary>
/// Atualizacao pelo proprio app, em cima do Velopack.
/// </summary>
/// <remarks>
/// POR QUE VELOPACK E NAO O QUE ESTAVA AQUI: a versao anterior baixava o exe
/// inteiro e se trocava no lugar renomeando o proprio arquivo. Funcionava, mas
/// cada atualizacao custava 101MB — e 98 desses MB (runtime do .NET e motor de
/// video) sao identicos entre uma versao e a seguinte.
///
/// O Velopack faz patch binario (zstd) por ARQUIVO do pacote: mudanca so de
/// codigo baixa so o codigo. Ele tambem traz o instalador, a instalacao por
/// usuario e o ciclo de reinicio — coisas que eu teria que escrever e manter na
/// mao, cada uma com sua propria maneira de dar errado.
///
/// INSTALACAO POR USUARIO (%LOCALAPPDATA%), nao Program Files, de proposito: o
/// app troca o proprio binario ao atualizar, e em Program Files isso pediria
/// UAC toda vez. E o mesmo motivo do Discord e do VS Code instalarem assim.
///
/// O QUE O VELOPACK NAO RESOLVE: o SmartScreen. O aviso vem de o binario nao ser
/// assinado; instalador tambem nao assinado avisa igual. So certificado resolve,
/// e isso custa dinheiro por ano.
/// </remarks>
public static class Updater
{
    /// <summary>
    /// Repositorio de onde vem as versoes. So releases, publico, separado do
    /// codigo — ver o README do primicord-releases.
    /// </summary>
    public const string DefaultRepo = "BanePlayss/primicord-releases";

    /// <summary>Versao deste build, vinda do &lt;Version&gt; do csproj.</summary>
    public static string CurrentVersion
    {
        get
        {
            try
            {
                // O assembly DESTE tipo, e nao o de entrada: quando o Primicord e
                // carregado como biblioteca (teste, diagnostico), o de entrada e
                // outro e a conta sairia contra a versao do host.
                var asm = typeof(Updater).Assembly;
                string? v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                               ?.InformationalVersion;
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

    private static UpdateManager Manager(string repo)
        => new(new GithubSource($"https://github.com/{repo}", null, false));

    /// <summary>
    /// false quando rodando de dentro do projeto (dotnet run) ou de um exe solto:
    /// nao ha instalacao pra atualizar.
    /// </summary>
    public static bool CanSelfUpdate
    {
        get
        {
            try { return Manager(DefaultRepo).IsInstalled; }
            catch { return false; }
        }
    }

    /// <summary>Guarda o que foi encontrado, pro segundo clique saber o que instalar.</summary>
    private static UpdateInfo? _pendente;

    /// <summary>Procura versao nova. null = ja estamos na mais recente.</summary>
    public static async Task<UpdateInfoView?> CheckAsync(string repo, CancellationToken ct = default)
    {
        var mgr = Manager(repo);
        if (!mgr.IsInstalled)
            throw new InvalidOperationException(
                "Esta copia nao foi instalada — a troca automatica so funciona na versao instalada. "
              + "Baixa o instalador uma vez e depois nunca mais precisa.");

        var info = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
        if (info == null)
        {
            Log.Write($"update: {CurrentVersion} ja e a mais recente");
            _pendente = null;
            return null;
        }

        _pendente = info;

        // Com delta, o que se baixa e a soma dos patches — nao o pacote cheio.
        bool delta = info.DeltasToTarget is { Length: > 0 };
        long tamanho = delta
            ? info.DeltasToTarget.Sum(d => d.Size)
            : info.TargetFullRelease.Size;

        Log.Write($"update: {CurrentVersion} -> {info.TargetFullRelease.Version} "
                + $"({(delta ? "delta" : "pacote cheio")}, {tamanho / 1024 / 1024.0:0.0} MB)");

        return new UpdateInfoView
        {
            Version = info.TargetFullRelease.Version.ToString(),
            Size = tamanho,
            Notes = info.TargetFullRelease.NotesMarkdown ?? "",
            IsDelta = delta,
        };
    }

    /// <summary>
    /// Baixa e instala o que o <see cref="CheckAsync"/> achou, e reabre o app.
    /// Nao retorna: o processo e substituido.
    /// </summary>
    public static async Task DownloadAndApplyAsync(string repo, IProgress<int>? progress,
                                                   CancellationToken ct = default)
    {
        var info = _pendente
            ?? throw new InvalidOperationException("Procura a atualizacao antes de instalar.");

        var mgr = Manager(repo);
        await mgr.DownloadUpdatesAsync(info, p => progress?.Report(p), ct).ConfigureAwait(false);

        Log.Write("update: baixado, reiniciando na versao nova");
        // --updated: o processo novo sobe enquanto este ainda morre, e sem isso ele
        // bateria no mutex de instancia unica e sairia — deixando o usuario sem app
        // nenhum na tela depois de mandar atualizar.
        mgr.ApplyUpdatesAndRestart(info.TargetFullRelease, new[] { "--updated" });
    }
}
