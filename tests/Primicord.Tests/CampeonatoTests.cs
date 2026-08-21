using Primicord;
using Xunit;

namespace Primicord.Tests;

/// <summary>
/// A tabela tem que bater com a do site — numero divergente e pior que numero
/// nenhum, porque quem olhar vai achar que um dos dois esta quebrado.
/// </summary>
/// <remarks>
/// Os casos aqui sao os do computeStandings() do apostas-app.jsx: 3/1/0 e
/// desempate por pontos -> saldo -> vitorias -> gols pro -> nome.
/// </remarks>
public sealed class CampeonatoTests
{
    /// <summary>Monta o mesmo JSON que o doc primitivao/state carrega.</summary>
    private static Classificacao TabelaDe(string roundsJson, int rodadaAtual = 1)
    {
        var t = Campeonato.Calcular($"{{\"currentRound\":{rodadaAtual},\"rounds\":{roundsJson}}}");
        Assert.NotNull(t);
        return t!;
    }

    [Fact]
    public void Vitoria_vale_3_empate_1_derrota_0()
    {
        var t = TabelaDe("""
            [[{"home":"a","away":"b","gh":"2","ga":"1"},
              {"home":"c","away":"d","gh":"1","ga":"1"}]]
            """);

        var a = t.Times.Single(x => x.Nick == "a");
        var b = t.Times.Single(x => x.Nick == "b");
        var c = t.Times.Single(x => x.Nick == "c");

        Assert.Equal(3, a.P);
        Assert.Equal(0, b.P);
        Assert.Equal(1, c.P);
        Assert.Equal(1, a.V);
        Assert.Equal(1, b.D);
        Assert.Equal(1, c.E);
        Assert.Equal(1, a.Sg);
        Assert.Equal(-1, b.Sg);
    }

    [Fact]
    public void Empatados_em_pontos_desempatam_no_saldo()
    {
        // Os dois com 3 pontos; quem venceu por mais gols fica na frente.
        var t = TabelaDe("""
            [[{"home":"folgado","away":"x","gh":"5","ga":"0"},
              {"home":"apertado","away":"y","gh":"1","ga":"0"}]]
            """);

        Assert.Equal("folgado", t.Times[0].Nick);
        Assert.Equal("apertado", t.Times[1].Nick);
    }

    [Fact]
    public void Jogo_sem_placar_nao_conta_mas_o_time_aparece()
    {
        // Time que ainda nao jogou tem que estar na tabela com zeros, e nao sumir.
        var t = TabelaDe("""
            [[{"home":"jogou","away":"tambem","gh":"1","ga":"0"},
              {"home":"marcado","away":"esperando"}]]
            """);

        Assert.Equal(4, t.Times.Count);
        var esperando = t.Times.Single(x => x.Nick == "esperando");
        Assert.Equal(0, esperando.J);
        Assert.Equal(0, esperando.P);
        Assert.Equal("em andamento", t.Situacao);
    }

    [Fact]
    public void Placar_pela_metade_e_ignorado()
    {
        // No site os gols sao string; um lado preenchido e o outro nao e jogo
        // que ainda nao aconteceu, nao vitoria por W.O.
        var t = TabelaDe("""[[{"home":"a","away":"b","gh":"3","ga":""}]]""");

        Assert.All(t.Times, x => Assert.Equal(0, x.J));
        Assert.Equal("nao comecou", t.Situacao);
    }

    [Fact]
    public void Tudo_jogado_marca_encerrado()
    {
        var t = TabelaDe("""[[{"home":"a","away":"b","gh":"0","ga":"0"}]]""");
        Assert.Equal("encerrado", t.Situacao);
    }

    [Fact]
    public void Documento_sem_rodadas_nao_estoura()
    {
        Assert.Null(Campeonato.Calcular("{}"));
        Assert.Null(Campeonato.Calcular("nao e json"));
    }
}
