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
        };
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
    }
}
