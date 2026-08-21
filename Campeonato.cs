using System.Text.Json.Nodes;

namespace Primicord;

/// <summary>Uma linha da tabela.</summary>
public sealed class TimeNaTabela
{
    public string Nick = "";
    public int J, V, E, D, Gp, Gc, P;
    public int Sg => Gp - Gc;
}

/// <summary>A classificacao inteira, como o site mostra.</summary>
public sealed class Classificacao
{
    public int RodadaAtual;
    public int TotalRodadas;
    public readonly List<TimeNaTabela> Times = new();

    /// <summary>"nao comecou" | "em andamento" | "encerrado".</summary>
    public string Situacao = "nao comecou";

    public bool Vazia => Times.Count == 0;
}

/// <summary>
/// A tabela do campeonato do Primitivao, lida direto da fonte.
/// </summary>
/// <remarks>
/// DE ONDE VEM: o doc primitivao/state, que tem { currentRound, rounds } — as
/// rodadas com os placares. Sao ~4KB. O primitivao/apostas, que o login ja le,
/// tem 665KB (apostas, comentarios, noticias, copa); nada disso serve pra tabela,
/// entao ele nao e tocado aqui. Era a diferenca entre um poll barato e um caro.
///
/// O CALCULO E COPIA FIEL do computeStandings() do apostas-app.jsx, incluindo a
/// ordem de desempate: pontos -> saldo de gols -> vitorias -> gols pro -> nome.
/// Isso nao e capricho: numero que discorda do site e pior que numero nenhum —
/// quem olhar vai achar que um dos dois esta quebrado. Se a regra mudar la, muda
/// aqui; a fonte e aquele arquivo, nao a minha memoria de como futebol pontua.
///
/// SOMENTE LEITURA, como todo o resto que toca o Primitivao.
/// </remarks>
public static class Campeonato
{
    private const string DocPath = "primitivao/state";

    public static async Task<Classificacao?> LerAsync(Firestore fs, CancellationToken ct = default)
    {
        Dictionary<string, object?>? doc;
        try { doc = await fs.GetAsync(DocPath, ct).ConfigureAwait(false); }
        catch (Exception ex) { Log.Write("campeonato: nao li o state: " + ex.Message); return null; }
        if (doc == null) return null;
        return Calcular(Firestore.Str(doc, "json", "{}"));
    }

    /// <summary>
    /// A conta, separada do IO. Toda a logica que pode errar mora aqui, e aqui da
    /// pra testar contra os mesmos casos do site sem tocar na rede.
    /// </summary>
    public static Classificacao? Calcular(string json)
    {
        JsonNode? raiz;
        try { raiz = JsonNode.Parse(json); }
        catch (Exception ex) { Log.Write("campeonato: json invalido: " + ex.Message); return null; }

        var rodadas = raiz?["rounds"] as JsonArray;
        if (rodadas == null) return null;

        var tabela = new Classificacao
        {
            RodadaAtual = (int)(raiz?["currentRound"]?.GetValue<int>() ?? 0),
            TotalRodadas = rodadas.Count,
        };

        var reg = new Dictionary<string, TimeNaTabela>(StringComparer.OrdinalIgnoreCase);
        TimeNaTabela Garante(string id)
        {
            if (!reg.TryGetValue(id, out var t)) { t = new TimeNaTabela { Nick = id }; reg[id] = t; }
            return t;
        }

        int jogos = 0, jogados = 0;
        foreach (var rodada in rodadas)
        {
            if (rodada is not JsonArray jogosDaRodada) continue;
            foreach (var j in jogosDaRodada)
            {
                string? casa = j?["home"]?.GetValue<string>();
                string? fora = j?["away"]?.GetValue<string>();
                if (string.IsNullOrEmpty(casa) || string.IsNullOrEmpty(fora)) continue;

                // Garante presenca na tabela mesmo sem placar — time que ainda nao
                // jogou aparece com zeros, e nao some da lista.
                var H = Garante(casa);
                var A = Garante(fora);
                jogos++;

                if (!TentaPlacar(j, "gh", out int gh) || !TentaPlacar(j, "ga", out int ga)) continue;
                jogados++;

                H.J++; A.J++;
                H.Gp += gh; H.Gc += ga;
                A.Gp += ga; A.Gc += gh;
                if (gh > ga) { H.V++; A.D++; H.P += 3; }
                else if (gh < ga) { A.V++; H.D++; A.P += 3; }
                else { H.E++; A.E++; H.P += 1; A.P += 1; }
            }
        }

        tabela.Times.AddRange(reg.Values.OrderByDescending(t => t.P)
                                        .ThenByDescending(t => t.Sg)
                                        .ThenByDescending(t => t.V)
                                        .ThenByDescending(t => t.Gp)
                                        .ThenBy(t => t.Nick, StringComparer.OrdinalIgnoreCase));

        tabela.Situacao = jogados == 0 ? "nao comecou"
                        : jogados >= jogos ? "encerrado"
                        : "em andamento";
        return tabela;
    }

    /// <summary>
    /// Placar so conta quando os DOIS lados sao numero — no site os gols sao
    /// string ("4"), e jogo ainda nao jogado vem vazio ou ausente.
    /// </summary>
    private static bool TentaPlacar(JsonNode? jogo, string campo, out int valor)
    {
        valor = 0;
        var n = jogo?[campo];
        if (n == null) return false;
        try
        {
            if (n.GetValueKind() == System.Text.Json.JsonValueKind.Number) { valor = n.GetValue<int>(); return true; }
            return int.TryParse(n.GetValue<string>(), out valor);
        }
        catch { return false; }
    }
}
