using System.Text.Json.Nodes;
using Primicord;
using Xunit;

namespace Primicord.Tests;

/// <summary>
/// As odds. Elas tem que sair iguais as do site — quem apostar la e olhar aqui
/// nao pode ver numero diferente pro mesmo confronto.
/// </summary>
public sealed class ApostasLolTests
{
    private static readonly string[] Oito = { "a", "b", "c", "d", "e", "f", "g", "h" };

    private static JsonNode Placares(params (int R, int G, string W1, string W2)[] jogos)
    {
        var o = new JsonObject();
        foreach (var (r, g, w1, w2) in jogos)
            o[$"R{r}-{g}"] = new JsonObject { ["w1"] = w1, ["w2"] = w2 };
        return o;
    }

    [Theory]
    // odd = 1 + (1/p - 1) * 0.85, arredondada em 2 casas.
    [InlineData(0.5, 1.85)]
    [InlineData(0.25, 3.55)]
    [InlineData(0.75, 1.28)]
    public void Odd_segue_a_formula_do_site(double p, double esperada)
    {
        Assert.Equal(esperada, ApostasLol.Odd(p));
    }

    [Fact]
    public void Mercado_quase_certo_nao_e_oferecido()
    {
        // Acima de ~0.98 a odd cai abaixo de 1,02 e o site nao oferece.
        Assert.Null(ApostasLol.Odd(0.99));
        Assert.Null(ApostasLol.Odd(1.0));
        Assert.Null(ApostasLol.Odd(0));
        Assert.Null(ApostasLol.Odd(-1));
    }

    [Fact]
    public void Sem_historico_o_confronto_sai_parelho()
    {
        // p = 0.5 dos dois lados: 2x0 e 0x2 pagam igual, e o empate (2p*q = 0.5)
        // paga menos que os dois — que e o certo em MD2.
        var abertas = ApostasLol.Abertas(Oito, null, null, maximo: 1);

        var a = Assert.Single(abertas);
        Assert.Equal(a.OddCasa, a.OddFora);
        Assert.Equal(3.55, a.OddCasa);   // p=0.25 pro 2x0
        Assert.Equal(1.85, a.OddEmpate); // p=0.50 pro 1x1
    }

    [Fact]
    public void Confronto_com_resultado_sai_da_lista()
    {
        var primeiro = CampeonatoLol.RoundRobin(Oito)[0].Jogos[0];

        var abertas = ApostasLol.Abertas(Oito, Placares((1, 0, "H", "H")), null, maximo: 5);

        Assert.DoesNotContain(abertas, x => x.Casa == primeiro.Casa && x.Fora == primeiro.Fora);
    }

    [Fact]
    public void Placar_pela_metade_ja_fecha_a_aposta()
    {
        // Comecou a sair resultado, acabou a aposta — mesmo sem o confronto ter
        // terminado. Senao daria pra apostar sabendo metade.
        var meio = new JsonObject { ["R1-0"] = new JsonObject { ["w1"] = "H" } };
        var primeiro = CampeonatoLol.RoundRobin(Oito)[0].Jogos[0];

        var abertas = ApostasLol.Abertas(Oito, meio, null, maximo: 5);

        Assert.DoesNotContain(abertas, x => x.Casa == primeiro.Casa && x.Fora == primeiro.Fora);
    }

    [Fact]
    public void Trava_explicita_fecha_a_aposta()
    {
        var travas = new JsonObject { ["R1-0"] = true };
        var primeiro = CampeonatoLol.RoundRobin(Oito)[0].Jogos[0];

        var abertas = ApostasLol.Abertas(Oito, null, travas, maximo: 5);

        Assert.DoesNotContain(abertas, x => x.Casa == primeiro.Casa && x.Fora == primeiro.Fora);
    }

    [Fact]
    public void Quem_vem_ganhando_paga_menos()
    {
        // Monta um historico onde a casa do confronto alvo venceu tudo e o
        // visitante perdeu tudo: a odd da casa tem que ficar abaixo da do visitante.
        var rodadas = CampeonatoLol.RoundRobin(Oito);
        var placares = new JsonObject();
        for (int gi = 0; gi < rodadas[0].Jogos.Count; gi++)
            placares[$"R1-{gi}"] = new JsonObject { ["w1"] = "H", ["w2"] = "H" };

        var abertas = ApostasLol.Abertas(Oito, placares, null, maximo: 20);
        Assert.NotEmpty(abertas);

        // Em algum confronto aberto, alguem que venceu enfrenta alguem que perdeu.
        var vencedores = rodadas[0].Jogos.Select(j => j.Casa).ToHashSet();
        var desigual = abertas.FirstOrDefault(a =>
            vencedores.Contains(a.Casa) != vencedores.Contains(a.Fora));
        Assert.NotNull(desigual);

        bool casaVemGanhando = vencedores.Contains(desigual!.Casa);
        if (casaVemGanhando) Assert.True(desigual.OddCasa < desigual.OddFora);
        else Assert.True(desigual.OddFora < desigual.OddCasa);
    }

    [Fact]
    public void Gente_de_menos_nao_gera_aposta()
    {
        Assert.Empty(ApostasLol.Abertas(new[] { "so_um" }, null, null));
        Assert.Empty(ApostasLol.Abertas(Array.Empty<string>(), null, null));
    }
}
