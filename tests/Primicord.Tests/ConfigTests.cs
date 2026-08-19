using Primicord;
using Xunit;

namespace Primicord.Tests;

/// <summary>
/// O config no disco, com atencao especial ao caso que ja quebrou em campo:
/// valor velho no arquivo vencendo um padrao corrigido em versao nova.
/// </summary>
/// <remarks>
/// Entra na colecao serializada porque redireciona PRIMICORD_DATA_DIR, que e uma
/// variavel de ambiente do PROCESSO — dois testes destes em paralelo brigariam
/// pela mesma variavel.
/// </remarks>
[Collection("rede")]
public sealed class ConfigTests : IDisposable
{
    private readonly string _dir;
    private readonly string? _anterior;

    public ConfigTests()
    {
        _anterior = Environment.GetEnvironmentVariable("PRIMICORD_DATA_DIR");
        _dir = Path.Combine(Path.GetTempPath(), "primicord-cfg-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("PRIMICORD_DATA_DIR", _dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PRIMICORD_DATA_DIR", _anterior);
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Arquivo => Path.Combine(_dir, "config.txt");

    [Theory]
    [InlineData("100.64.2.73", "http://100.64.2.73:8765")]
    [InlineData("http://100.64.2.73", "http://100.64.2.73:8765")]
    [InlineData("pc-bane.tailnet.ts.net:9000", "http://pc-bane.tailnet.ts.net:9000")]
    [InlineData("http://primicord-server:8765/", "http://primicord-server:8765")]
    public void Normaliza_endereco_do_mini_servidor(string input, string expected)
        => Assert.Equal(expected, Config.NormalizeCoordServerUrl(input));

    [Fact]
    public void Config_antigo_sem_porta_e_migrado_ao_carregar()
    {
        File.WriteAllText(Arquivo, "coordserver=100.64.2.73");

        Assert.Equal("http://100.64.2.73:8765", Config.Load().CoordServerUrl);
    }

    [Fact]
    public void Repo_de_update_padrao_nao_e_gravado()
    {
        // Nao gravar e o que permite um padrao corrigido chegar em quem nunca
        // personalizou nada.
        new Config().Save();

        Assert.DoesNotContain("updaterepo=", File.ReadAllText(Arquivo));
        Assert.Equal(Updater.DefaultRepo, Config.Load().UpdateRepo);
    }

    [Fact]
    public void Repo_de_update_trocado_a_mao_e_preservado()
    {
        new Config { UpdateRepo = "outro/repositorio" }.Save();

        Assert.Contains("updaterepo=outro/repositorio", File.ReadAllText(Arquivo));
        Assert.Equal("outro/repositorio", Config.Load().UpdateRepo);
    }

    [Fact]
    public void Padrao_novo_alcanca_quem_so_clicou_em_salvar()
    {
        // O bug de verdade, reproduzido: config gravado por uma versao antiga com
        // um padrao que hoje esta errado. Salvar de novo tem que LIMPAR a linha,
        // e nao perpetua-la.
        File.WriteAllLines(Arquivo, new[] { "nick=bane", "updaterepo=BanePlayss/primicord" });

        var carregado = Config.Load();
        Assert.Equal("BanePlayss/primicord", carregado.UpdateRepo);   // ainda preso ao velho

        // Usuario abre as configuracoes e salva, sem tocar em nada de update.
        carregado.UpdateRepo = Updater.DefaultRepo;
        carregado.Save();

        Assert.DoesNotContain("updaterepo=", File.ReadAllText(Arquivo));
        Assert.Equal(Updater.DefaultRepo, Config.Load().UpdateRepo);
    }

    [Fact]
    public void Ida_e_volta_preserva_o_resto()
    {
        var original = new Config
        {
            Nick = "caco", MicDevice = 2, ClipSeconds = 45, ScreenBudgetKb = 900,
            UseWebRtc = true, EchoCancel = false, MicAutoGain = true,
            CamDevice = "Logi C270 HD WebCam", CamWidth = 640, CamHeight = 480, CamFps = 20,
            CachedPc = 1234, CachedCc = 77, CachedTeamId = "caco",
            CachedTeamName = "Caco", CachedThemeId = "oceano", CachedIsMod = true,
            CoordServerUrl = "http://100.90.80.70:8765", HostMiniServer = true,
        };
        original.SetPeerVolume("mohamed", 45);
        original.Save();

        var lido = Config.Load();
        Assert.Equal("caco", lido.Nick);
        Assert.Equal(2, lido.MicDevice);
        Assert.Equal(45, lido.ClipSeconds);
        Assert.Equal(900, lido.ScreenBudgetKb);
        Assert.True(lido.UseWebRtc);
        Assert.False(lido.EchoCancel);
        Assert.True(lido.MicAutoGain);
        Assert.Equal("Logi C270 HD WebCam", lido.CamDevice);
        Assert.Equal(640, lido.CamWidth);
        Assert.Equal(480, lido.CamHeight);
        Assert.Equal(20, lido.CamFps);
        Assert.Equal(1234, lido.CachedPc);
        Assert.Equal(77, lido.CachedCc);
        Assert.Equal("caco", lido.CachedTeamId);
        Assert.Equal("Caco", lido.CachedTeamName);
        Assert.Equal("oceano", lido.CachedThemeId);
        Assert.True(lido.CachedIsMod);
        Assert.Equal("http://100.90.80.70:8765", lido.CoordServerUrl);
        Assert.True(lido.HostMiniServer);
        Assert.Equal(45, lido.PeerVolume("MOHAMED"));
        Assert.Equal(100, lido.PeerVolume("vitinho"));
    }

    [Fact]
    public void Perfil_salvo_so_aceita_mesmo_nick_e_mesma_senha()
    {
        string hash = Primitivao.HashPassword("segredo");
        var cfg = new Config
        {
            Nick = "bane", SenhaHash = hash, CachedPc = 331_000,
            CachedThemeId = "oceano", CachedIsMod = true,
        };

        var cached = Primitivao.AuthenticateCached(cfg, "@BANE", hash);
        Assert.NotNull(cached);
        Assert.Equal("bane", cached.Nick);
        Assert.Equal(331_000, cached.Pc);
        Assert.Equal("oceano", cached.ThemeId);
        Assert.True(cached.IsMod);

        Assert.Null(Primitivao.AuthenticateCached(cfg, "ricle", hash));
        Assert.Null(Primitivao.AuthenticateCached(cfg, "bane", Primitivao.HashPassword("errada")));
    }

    [Fact]
    public async Task Perfil_salvo_e_usado_antes_do_servidor_remoto()
    {
        string hash = Primitivao.HashPassword("segredo");
        var cfg = new Config { Nick = "bane", SenhaHash = hash, CachedPc = 123 };
        int remoteCalls = 0;

        var result = await Primitivao.AuthenticatePreferCachedAsync(
            cfg, "BANE", hash,
            () =>
            {
                remoteCalls++;
                return Task.FromResult(new Primitivao.AuthResult(null, "nao deveria chamar"));
            });

        Assert.True(result.Ok);
        Assert.True(result.FromCache);
        Assert.Equal(0, remoteCalls);
        Assert.Equal(123, result.User!.Pc);
    }

    [Fact]
    public async Task Credencial_sem_cache_ainda_consulta_o_servidor_remoto()
    {
        string savedHash = Primitivao.HashPassword("segredo");
        var cfg = new Config { Nick = "bane", SenhaHash = savedHash };
        int remoteCalls = 0;

        var result = await Primitivao.AuthenticatePreferCachedAsync(
            cfg, "ricle", Primitivao.HashPassword("outra senha"),
            () =>
            {
                remoteCalls++;
                return Task.FromResult(new Primitivao.AuthResult(
                    new PrimitivaoUser { Nick = "ricle" }, null));
            });

        Assert.True(result.Ok);
        Assert.False(result.FromCache);
        Assert.Equal(1, remoteCalls);
        Assert.Equal("ricle", result.User!.Nick);
    }
}
