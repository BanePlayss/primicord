using System.Text.Json.Nodes;

namespace Primicord;

/// <summary>Um confronto aberto pra aposta, com as odds do resultado.</summary>
public sealed class ApostaLol
{
    public int Rodada;
    public string Casa = "";
    public string Fora = "";

    /// <summary>Odds do mercado RESULTADO: vitoria da casa, empate, vitoria do visitante.</summary>
    public double? OddCasa, OddEmpate, OddFora;
}

/// <summary>
/// As odds do LoL, calculadas do mesmo jeito que o site.
/// </summary>
/// <remarks>
/// A CADEIA, copiada do apostas-app.jsx:
///   forma        = partidas vencidas / partidas jogadas (null se ainda nao jogou)
///   p(vencer 1)  = 0.5 + (formaCasa - formaFora) * 0.6, preso entre 0.05 e 0.92
///   MD2          = 2x0 vale p², 1x1 vale 2p(1-p), 0x2 vale (1-p)²
///   odd          = 1 + (1/p - 1) * 0.85          (0.85 e a margem da casa)
///
/// Os numeros importam: 0.6 atenua porque MD2 blind pick e volatil, e o teto de
/// 0.92 impede odd absurda em cima de amostra de duas partidas. Sao decisoes do
/// site, nao minhas — se eu "melhorasse" alguma, as odds daqui parariam de bater
/// com as de la, e quem apostasse pelo site veria numero diferente.
///
/// TRES RESULTADOS, e nao dois: o rascunho mostrava dois numeros por confronto,
/// mas MD2 empata em 1x1 e o empate paga. Mostrar so casa e fora esconderia um
/// desfecho que acontece — e, nos dados de hoje, acontece bastante.
/// </remarks>
public static class ApostasLol
{
    private const double K = 0.85;
    private const double AtenuacaoForma = 0.6;
    private const double PMin = 0.05, PMax = 0.92;

    /// <summary>Campos que, preenchidos, ja fecham a aposta daquele confronto.</summary>
    private static readonly string[] CamposDoPlacar = { "w1", "m1", "w2", "m2" };

    /// <summary>
    /// Os confrontos ainda abertos, na ordem das rodadas.
    /// </summary>
    public static List<ApostaLol> Abertas(IReadOnlyList<string> jogadores, JsonNode? placares,
                                          JsonNode? travas, int maximo = 3)
    {
        var saida = new List<ApostaLol>();
        if (jogadores.Count < 2) return saida;

        var rodadas = CampeonatoLol.RoundRobin(jogadores);
        foreach (var (n, jogos) in rodadas)
        {
            for (int gi = 0; gi < jogos.Count && saida.Count < maximo; gi++)
            {
                string chave = $"R{n}-{gi}";
                if (Fechada(chave, placares, travas)) continue;

                var (casa, fora) = jogos[gi];
                double p = ProbCasa(jogadores, rodadas, placares, casa, fora);
                double q = 1 - p;
                saida.Add(new ApostaLol
                {
                    Rodada = n,
                    Casa = casa,
                    Fora = fora,
                    OddCasa = Odd(p * p),
                    OddEmpate = Odd(2 * p * q),
                    OddFora = Odd(q * q),
                });
            }
            if (saida.Count >= maximo) break;
        }
        return saida;
    }

    /// <summary>
    /// Aposta fecha quando ha trava explicita OU quando qualquer campo do placar
    /// ja foi preenchido — comecou a sair resultado, acabou a aposta.
    /// </summary>
    private static bool Fechada(string chave, JsonNode? placares, JsonNode? travas)
    {
        try { if (travas?[chave]?.GetValue<bool>() == true) return true; }
        catch { }

        var sc = placares?[chave];
        if (sc == null) return false;
        foreach (string campo in CamposDoPlacar)
        {
            var v = sc[campo];
            if (v == null) continue;
            try { if (!string.IsNullOrEmpty(v.GetValue<string>())) return true; }
            catch { return true; }   // preenchido com outro tipo tambem conta
        }
        return false;
    }

    /// <summary>Probabilidade da casa vencer UMA partida.</summary>
    private static double ProbCasa(IReadOnlyList<string> jogadores,
                                   List<(int N, List<(string Casa, string Fora)> Jogos)> rodadas,
                                   JsonNode? placares, string casa, string fora)
    {
        double? fc = Forma(rodadas, placares, casa);
        double? ff = Forma(rodadas, placares, fora);
        if (fc == null && ff == null) return 0.5;   // sem historico, parelho

        double a = fc ?? 0.5, b = ff ?? 0.5;
        if (a + b <= 0) return 0.5;
        return Math.Max(PMin, Math.Min(PMax, 0.5 + (a - b) * AtenuacaoForma));
    }

    /// <summary>Taxa de PARTIDAS vencidas na fase de grupos. null = ainda nao jogou.</summary>
    private static double? Forma(List<(int N, List<(string Casa, string Fora)> Jogos)> rodadas,
                                 JsonNode? placares, string nick)
    {
        int venceu = 0, total = 0;
        foreach (var (n, jogos) in rodadas)
        {
            for (int gi = 0; gi < jogos.Count; gi++)
            {
                var (casa, fora) = jogos[gi];
                bool souCasa = casa == nick;
                if (!souCasa && fora != nick) continue;

                var sc = placares?[$"R{n}-{gi}"];
                if (!Md2(sc, out int pvCasa, out int pvFora)) continue;

                venceu += souCasa ? pvCasa : pvFora;
                total += 2;
            }
        }
        return total > 0 ? venceu / (double)total : null;
    }

    private static bool Md2(JsonNode? sc, out int pvCasa, out int pvFora)
    {
        pvCasa = pvFora = 0;
        if (sc == null) return false;
        string? w1 = Lado(sc["w1"]), w2 = Lado(sc["w2"]);
        if (w1 == null || w2 == null) return false;
        pvCasa = (w1 == "H" ? 1 : 0) + (w2 == "H" ? 1 : 0);
        pvFora = 2 - pvCasa;
        return true;
    }

    private static string? Lado(JsonNode? n)
    {
        try
        {
            string? v = n?.GetValue<string>();
            return v is "H" or "A" ? v : null;
        }
        catch { return null; }
    }

    /// <summary>null quando o mercado nao e oferecido (quase-certo, odd &lt; 1,02).</summary>
    public static double? Odd(double p)
    {
        if (!(p > 0) || p >= 1) return null;
        double o = 1 + (1 / p - 1) * K;
        if (o < 1.02) return null;
        return Math.Round(o, 2);
    }
}
