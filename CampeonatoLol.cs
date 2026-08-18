using System.Text.Json.Nodes;

namespace Primicord;

/// <summary>
/// A classificacao do campeonato de LoL do Primitivao.
/// </summary>
/// <remarks>
/// COMO O SITE MONTA ISSO, e por isso e diferente do FIFA: a tabela do LoL nao
/// esta guardada em lugar nenhum. O que existe no Firestore e (a) quem se
/// inscreveu, no campo interests.lol, e (b) os placares, em json.lol.scores. As
/// RODADAS sao calculadas na hora, por todos-contra-todos (metodo do circulo), a
/// partir da lista de inscritos ordenada. Entao pra ter a tabela e preciso refazer
/// o mesmo sorteio que o site faz — se a geracao divergir num detalhe, os
/// confrontos saem trocados e a tabela fica errada sem parecer errada.
///
/// CADA CONFRONTO E MD2: dois jogos, cada um com vencedor H ou A. 2x0 e vitoria
/// (3 pontos), 1x1 e empate (1 ponto). O "saldo" da tabela e saldo de PARTIDAS,
/// nao de gols — por isso reaproveita os mesmos campos Gp/Gc.
///
/// PESO: le o doc de apostas com mascara, so json e interests — 134KB em vez dos
/// 665KB do documento inteiro. Ainda assim e ~30x o doc do FIFA, entao o poll
/// aqui e mais espacado.
/// </remarks>
public static class CampeonatoLol
{
    private const string DocPath = "primitivao/apostas";

    public static async Task<Classificacao?> LerAsync(Firestore fs, CancellationToken ct = default)
    {
        JsonNode? raw;
        try { raw = await fs.GetRawAsync(DocPath, new[] { "json", "interests" }, ct).ConfigureAwait(false); }
        catch (Exception ex) { Log.Write("lol: nao li o doc: " + ex.Message); return null; }
        if (raw == null) return null;

        var campos = raw["fields"];

        // interests.lol -> quem se inscreveu. Ordenado, que e a semente do sorteio.
        var inscritos = campos?["interests"]?["mapValue"]?["fields"]?["lol"]?["mapValue"]?["fields"];
        if (inscritos is not JsonObject mapa) return null;
        var jogadores = mapa.Select(kv => kv.Key)
                            .OrderBy(n => n, StringComparer.Ordinal)
                            .ToList();

        JsonNode? placares = null;
        try
        {
            string json = campos?["json"]?["stringValue"]?.GetValue<string>() ?? "{}";
            placares = JsonNode.Parse(json)?["lol"]?["scores"];
        }
        catch (Exception ex) { Log.Write("lol: placares ilegiveis: " + ex.Message); }

        return Calcular(jogadores, placares);
    }

    /// <summary>A conta, separada do IO — e aqui que mora tudo que pode errar.</summary>
    public static Classificacao? Calcular(IReadOnlyList<string> jogadores, JsonNode? placares)
    {
        if (jogadores.Count < 2) return null;

        var rodadas = RoundRobin(jogadores);
        var reg = new Dictionary<string, TimeNaTabela>(StringComparer.Ordinal);
        foreach (var j in jogadores) reg[j] = new TimeNaTabela { Nick = j };

        // Quem ganhou o confronto direto, pro desempate. Vazio = empatou.
        var confrontoDireto = new Dictionary<string, string>(StringComparer.Ordinal);
        int jogados = 0, total = 0, ultimaRodadaComJogo = 0;

        foreach (var (n, jogos) in rodadas)
        {
            bool algumNaRodada = false;
            for (int gi = 0; gi < jogos.Count; gi++)
            {
                var (casa, fora) = jogos[gi];
                total++;
                var sc = placares?[$"R{n}-{gi}"];
                if (!Resultado(sc, out int pvCasa, out int pvFora)) continue;

                jogados++;
                algumNaRodada = true;
                var H = reg[casa];
                var A = reg[fora];

                H.J++; A.J++;
                H.Gp += pvCasa; H.Gc += pvFora;
                A.Gp += pvFora; A.Gc += pvCasa;

                if (pvCasa > pvFora) { H.V++; A.D++; H.P += 3; confrontoDireto[Par(casa, fora)] = casa; }
                else if (pvCasa < pvFora) { A.V++; H.D++; A.P += 3; confrontoDireto[Par(casa, fora)] = fora; }
                else { H.E++; A.E++; H.P += 1; A.P += 1; confrontoDireto[Par(casa, fora)] = ""; }
            }
            if (algumNaRodada) ultimaRodadaComJogo = n;
        }

        var tabela = new Classificacao
        {
            TotalRodadas = rodadas.Count,
            RodadaAtual = Math.Min(ultimaRodadaComJogo + (jogados < total ? 1 : 0), rodadas.Count),
            Situacao = jogados == 0 ? "nao comecou" : jogados >= total ? "encerrado" : "em andamento",
        };

        // Ordena pelos criterios TRANSITIVOS primeiro. O confronto direto do site
        // e criterio de par: dentro de um Sort geral ele pode gerar comparacao
        // inconsistente (A>B, B>C, C>A) e o .NET aborta em tempo de execucao.
        // Entao ele entra depois, so entre vizinhos empatados em todo o resto —
        // que e exatamente quando ele decide alguma coisa.
        var ordenada = reg.Values
            .OrderByDescending(t => t.P)
            .ThenByDescending(t => t.Sg)
            .ThenByDescending(t => t.Gp)
            .ThenBy(t => t.Nick, StringComparer.Ordinal)
            .ToList();

        for (int i = 0; i + 1 < ordenada.Count; i++)
        {
            var a = ordenada[i];
            var b = ordenada[i + 1];
            if (a.P != b.P || a.Sg != b.Sg || a.Gp != b.Gp) continue;
            if (confrontoDireto.TryGetValue(Par(a.Nick, b.Nick), out var venceu) && venceu == b.Nick)
                (ordenada[i], ordenada[i + 1]) = (b, a);
        }

        tabela.Times.AddRange(ordenada);
        return tabela;
    }

    private static string Par(string a, string b)
        => string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;

    /// <summary>
    /// MD2: so conta quando os DOIS jogos tem vencedor. Confronto pela metade e
    /// confronto que ainda nao terminou, nao vitoria de quem ganhou o primeiro.
    /// </summary>
    private static bool Resultado(JsonNode? sc, out int pvCasa, out int pvFora)
    {
        pvCasa = pvFora = 0;
        if (sc == null) return false;
        string? w1 = Lado(sc["w1"]);
        string? w2 = Lado(sc["w2"]);
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

    /// <summary>
    /// Todos-contra-todos pelo metodo do circulo — copia do lolRoundRobin() do
    /// site, inclusive a alternancia de mando por rodada. Numero impar de
    /// jogadores gera folga, e o jogo com folga simplesmente nao existe.
    /// </summary>
    public static List<(int N, List<(string Casa, string Fora)> Jogos)> RoundRobin(
        IReadOnlyList<string> jogadores)
    {
        var saida = new List<(int, List<(string, string)>)>();
        if (jogadores.Count < 2) return saida;

        var ps = jogadores.Select(j => (string?)j).ToList();
        if (ps.Count % 2 == 1) ps.Add(null);   // folga

        int n = ps.Count;
        var arr = ps.ToList();
        for (int r = 0; r < n - 1; r++)
        {
            var jogos = new List<(string, string)>();
            for (int i = 0; i < n / 2; i++)
            {
                string? a = arr[i], b = arr[n - 1 - i];
                if (a == null || b == null) continue;
                jogos.Add(r % 2 == 0 ? (a, b) : (b, a));
            }
            saida.Add((r + 1, jogos));

            // Fixa o primeiro e rotaciona o resto.
            var fixo = arr[0];
            var resto = arr.Skip(1).ToList();
            var ultimo = resto[^1];
            resto.RemoveAt(resto.Count - 1);
            resto.Insert(0, ultimo);
            arr = new List<string?> { fixo };
            arr.AddRange(resto);
        }
        return saida;
    }
}
