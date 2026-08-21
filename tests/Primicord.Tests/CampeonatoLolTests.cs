using System.Text.Json.Nodes;
using Primicord;
using Xunit;

namespace Primicord.Tests;

/// <summary>
/// A tabela do LoL. O risco aqui e maior que no FIFA: as rodadas nao vem prontas
/// do banco, sao SORTEADAS na hora pelo mesmo metodo do site. Se o sorteio
/// divergir, os confrontos saem trocados e a tabela fica errada sem parecer errada.
/// </summary>
public sealed class CampeonatoLolTests
{
    private static readonly string[] Oito =
        { "a", "b", "c", "d", "e", "f", "g", "h" };

    private static JsonNode Placares(params (int Rodada, int Jogo, string W1, string W2)[] jogos)
    {
        var o = new JsonObject();
        foreach (var (r, g, w1, w2) in jogos)
            o[$"R{r}-{g}"] = new JsonObject { ["w1"] = w1, ["w2"] = w2 };
        return o;
    }

    [Fact]
    public void Todos_jogam_contra_todos_uma_vez_so()
    {
        var rodadas = CampeonatoLol.RoundRobin(Oito);

        Assert.Equal(7, rodadas.Count);
        Assert.All(rodadas, r => Assert.Equal(4, r.Jogos.Count));

        // Cada par se encontra exatamente uma vez.
        var pares = rodadas.SelectMany(r => r.Jogos)
                           .Select(j => string.CompareOrdinal(j.Casa, j.Fora) < 0
                                      ? j.Casa + j.Fora : j.Fora + j.Casa)
                           .ToList();
        Assert.Equal(28, pares.Count);
        Assert.Equal(28, pares.Distinct().Count());

        // E ninguem joga duas vezes na mesma rodada.
        foreach (var r in rodadas)
        {
            var gente = r.Jogos.SelectMany(j => new[] { j.Casa, j.Fora }).ToList();
            Assert.Equal(gente.Count, gente.Distinct().Count());
        }
    }

    [Fact]
    public void Numero_impar_gera_folga_e_ninguem_some()
    {
        var rodadas = CampeonatoLol.RoundRobin(new[] { "a", "b", "c", "d", "e" });

        Assert.Equal(5, rodadas.Count);
        Assert.All(rodadas, r => Assert.Equal(2, r.Jogos.Count));   // um fica de folga

        var todos = rodadas.SelectMany(r => r.Jogos).SelectMany(j => new[] { j.Casa, j.Fora }).Distinct();
        Assert.Equal(5, todos.Count());
    }

    [Fact]
    public void Md2_dois_a_zero_e_vitoria_e_um_a_um_e_empate()
    {
        var r1 = CampeonatoLol.RoundRobin(Oito)[0];
        var (casa0, fora0) = r1.Jogos[0];
        var (casa1, fora1) = r1.Jogos[1];

        var t = CampeonatoLol.Calcular(Oito, Placares(
            (1, 0, "H", "H"),    // casa0 vence 2x0
            (1, 1, "H", "A")));  // 1x1, empate
        Assert.NotNull(t);

        var vencedor = t!.Times.Single(x => x.Nick == casa0);
        var perdedor = t.Times.Single(x => x.Nick == fora0);
        Assert.Equal(3, vencedor.P);
        Assert.Equal(2, vencedor.Gp);
        Assert.Equal(0, perdedor.P);

        foreach (var nick in new[] { casa1, fora1 })
        {
            var e = t.Times.Single(x => x.Nick == nick);
            Assert.Equal(1, e.P);
            Assert.Equal(1, e.E);
            Assert.Equal(0, e.Sg);
        }
    }

    [Fact]
    public void Confronto_pela_metade_nao_conta()
    {
        // So o primeiro jogo do MD2 saiu: o confronto ainda nao terminou, e nao
        // e vitoria de quem ganhou a primeira.
        var so_w1 = new JsonObject { ["R1-0"] = new JsonObject { ["w1"] = "H" } };

        var t = CampeonatoLol.Calcular(Oito, so_w1);

        Assert.NotNull(t);
        Assert.All(t!.Times, x => Assert.Equal(0, x.J));
        Assert.Equal("nao comecou", t.Situacao);
    }

    [Fact]
    public void Situacao_acompanha_o_andamento()
    {
        Assert.Equal("nao comecou", CampeonatoLol.Calcular(Oito, null)!.Situacao);
        Assert.Equal("em andamento", CampeonatoLol.Calcular(Oito, Placares((1, 0, "H", "H")))!.Situacao);

        // Todos os 28 confrontos com resultado.
        var tudo = new JsonObject();
        foreach (var r in CampeonatoLol.RoundRobin(Oito))
            for (int g = 0; g < r.Jogos.Count; g++)
                tudo[$"R{r.N}-{g}"] = new JsonObject { ["w1"] = "H", ["w2"] = "H" };
        Assert.Equal("encerrado", CampeonatoLol.Calcular(Oito, tudo)!.Situacao);
    }

    [Fact]
    public void Confronto_direto_desempata_quem_empatou_em_tudo()
    {
        // Dois jogadores, um confronto: quem ganhou fica na frente mesmo que o
        // nome do outro venha antes no alfabeto.
        var t = CampeonatoLol.Calcular(new[] { "aaa", "zzz" }, Placares((1, 0, "A", "A")));

        Assert.NotNull(t);
        var jogo = CampeonatoLol.RoundRobin(new[] { "aaa", "zzz" })[0].Jogos[0];
        Assert.Equal(jogo.Fora, t!.Times[0].Nick);   // venceu como visitante
        Assert.Equal(3, t.Times[0].P);
    }

    [Fact]
    public void Gente_de_menos_nao_vira_tabela()
    {
        Assert.Null(CampeonatoLol.Calcular(new[] { "sozinho" }, null));
        Assert.Null(CampeonatoLol.Calcular(Array.Empty<string>(), null));
        Assert.Empty(CampeonatoLol.RoundRobin(new[] { "sozinho" }));
    }
}
