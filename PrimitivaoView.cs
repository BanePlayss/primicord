using System.Drawing.Drawing2D;

namespace Primicord;

/// <summary>
/// O Primitivao dentro do Primicord: campeonato, confrontos abertos e a manchete.
/// </summary>
/// <remarks>
/// E o que ocupa o centro quando nao se esta numa sala de voz. Antes ali ficava o
/// chat ocupando a tela inteira — um canal de texto que quase nao se usa, porque
/// quem esta junto no Primicord esta falando, nao digitando. O chat continua
/// existindo a um clique no rail; o que mudou e o que ganha a tela por padrao.
///
/// Desenhado num controle so, como o resto: sao tres cartoes de texto que nunca
/// recebem clique, e um controle por linha seria dezenas de handles pra nada.
/// </remarks>
public sealed class PrimitivaoView : Control
{
    private CampeonatoLol.PainelPrimitivao? _dados;

    public string MeuNick = "";

    public PrimitivaoView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Pv.Charcoal;
    }

    public void Definir(CampeonatoLol.PainelPrimitivao? dados)
    {
        _dados = dados;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using (var fundo = new SolidBrush(Pv.Charcoal)) g.FillRectangle(fundo, ClientRectangle);

        int x = 28, largura = Math.Max(320, Width - 56);
        int y = 24;

        // ── titulo ──
        using (var b = new SolidBrush(Pv.Orange))
            Pv.DrawTracked(g, "PRIMITIVAO", Pv.DisplaySm, b, x, y, 2.2f);
        y += 30;

        var d = _dados;
        if (d == null)
        {
            using var cinza = new SolidBrush(Pv.BoneDim);
            Pv.DrawTracked(g, "CARREGANDO...", Pv.Label, cinza, x, y, 1.4f);
            return;
        }

        using (var b = new SolidBrush(Pv.BoneDim))
        {
            string sub = d.MeuPc > 0
                ? $"{MeuNick.ToUpperInvariant()} · {PrimitivaoUser.Compact(d.MeuPc)} PC"
                : "campeonato, apostas e o que rolou";
            Pv.DrawTracked(g, sub, Pv.Label, b, x, y, 1.4f);
        }
        y += 30;

        y = Tabela(g, d, x, y, largura);
        y = Confrontos(g, d, x, y + 18, largura);
        Manchete(g, d, x, y + 18, largura);
    }

    // ─── cartao: a tabela inteira ────────────────────────────────────────────

    private int Tabela(Graphics g, CampeonatoLol.PainelPrimitivao d, int x, int y, int largura)
    {
        var t = d.Tabela;
        int linhas = t?.Times.Count ?? 0;
        int altura = 44 + Math.Max(1, linhas) * 22 + 12;
        Cartao(g, x, y, largura, altura);

        string titulo = t == null ? "CAMPEONATO"
            : t.Situacao == "encerrado" ? "LOL · ENCERRADO"
            : t.Situacao == "nao comecou" ? "LOL · AINDA NAO COMECOU"
            : $"LOL · RODADA {t.RodadaAtual} DE {t.TotalRodadas}";
        Titulo(g, titulo, x + 18, y + 14);

        if (t == null || t.Times.Count == 0)
        {
            using var cinza = new SolidBrush(Pv.BoneDim);
            Pv.DrawTracked(g, "sem tabela", Pv.Label, cinza, x + 18, y + 44, 1.2f);
            return y + altura;
        }

        // Colunas: aqui ha espaco pra mostrar tudo, ao contrario da lateral.
        int cJ = x + largura - 200, cV = x + largura - 164, cE = x + largura - 132,
            cD = x + largura - 100, cSg = x + largura - 62, cP = x + largura - 22;
        int ly = y + 40;

        using (var cab = new SolidBrush(Pv.BoneDim))
        {
            Col(g, "J", cJ, ly, cab); Col(g, "V", cV, ly, cab); Col(g, "E", cE, ly, cab);
            Col(g, "D", cD, ly, cab); Col(g, "SG", cSg, ly, cab); Col(g, "P", cP, ly, cab);
        }
        ly += 18;

        for (int i = 0; i < t.Times.Count; i++)
        {
            var time = t.Times[i];
            bool eu = time.Nick.Equals(MeuNick, StringComparison.OrdinalIgnoreCase);
            if (eu)
                using (var faixa = new SolidBrush(Pv.Char3))
                    g.FillRectangle(faixa, x + 10, ly - 3, largura - 20, 21);

            Color cor = i == 0 ? Pv.Orange : (eu ? Pv.Bone : Pv.BoneDim);
            using var b = new SolidBrush(cor);
            Pv.DrawTracked(g, (i + 1).ToString(), Pv.Label, b, x + 18, ly, 1.0f);
            Pv.DrawTracked(g, time.Nick.ToUpperInvariant(), Pv.Label, b, x + 44, ly, 1.0f);
            Col(g, time.J.ToString(), cJ, ly, b);
            Col(g, time.V.ToString(), cV, ly, b);
            Col(g, time.E.ToString(), cE, ly, b);
            Col(g, time.D.ToString(), cD, ly, b);
            Col(g, time.Sg > 0 ? "+" + time.Sg : time.Sg.ToString(), cSg, ly, b);
            Col(g, time.P.ToString(), cP, ly, b);
            ly += 22;
        }
        return y + altura;
    }

    // ─── cartao: confrontos abertos ──────────────────────────────────────────

    private int Confrontos(Graphics g, CampeonatoLol.PainelPrimitivao d, int x, int y, int largura)
    {
        int n = d.Apostas.Count;
        int altura = 44 + Math.Max(1, n) * 26 + 12;
        Cartao(g, x, y, largura, altura);
        Titulo(g, "APOSTAS DISPONIVEIS", x + 18, y + 14);

        if (n == 0)
        {
            using var cinza = new SolidBrush(Pv.BoneDim);
            Pv.DrawTracked(g, "nenhum confronto aberto agora", Pv.Label, cinza, x + 18, y + 44, 1.2f);
            return y + altura;
        }

        int ly = y + 44;
        int cCasa = x + largura - 210, cEmp = x + largura - 130, cFora = x + largura - 50;
        foreach (var a in d.Apostas)
        {
            using var b = new SolidBrush(Pv.Bone);
            using var bx = new SolidBrush(Pv.BoneDim);
            Pv.DrawTracked(g, $"{a.Casa.ToUpperInvariant()}  x  {a.Fora.ToUpperInvariant()}",
                           Pv.Label, b, x + 18, ly, 1.0f);
            Odd(g, a.OddCasa, cCasa, ly);
            Odd(g, a.OddEmpate, cEmp, ly);
            Odd(g, a.OddFora, cFora, ly);
            ly += 26;
        }
        return y + altura;
    }

    // ─── cartao: manchete ────────────────────────────────────────────────────

    private void Manchete(Graphics g, CampeonatoLol.PainelPrimitivao d, int x, int y, int largura)
    {
        var n = d.Ultima;
        if (n == null) return;

        int altura = 96;
        Cartao(g, x, y, largura, altura);
        Titulo(g, n.Tag.Length > 0 ? n.Tag.ToUpperInvariant() : "NOTICIAS", x + 18, y + 14);

        using (var b = new SolidBrush(Pv.Bone))
            Pv.DrawTracked(g, Cortar(g, n.Titulo.ToUpperInvariant(), largura - 40, Pv.Body, 1.0f),
                           Pv.Body, b, x + 18, y + 40, 1.0f);

        using (var b = new SolidBrush(Pv.BoneDim))
            Pv.DrawTracked(g, Cortar(g, n.Resumo, largura - 40, Pv.Label, 1.0f),
                           Pv.Label, b, x + 18, y + 66, 1.0f);
    }

    // ─── pinceis ─────────────────────────────────────────────────────────────

    private static void Cartao(Graphics g, int x, int y, int w, int h)
    {
        using var fundo = new SolidBrush(Pv.Char2);
        g.FillRectangle(fundo, x, y, w, h);
        using var borda = new Pen(Pv.Char3, 2);
        g.DrawRectangle(borda, x, y, w, h);
    }

    private static void Titulo(Graphics g, string t, int x, int y)
    {
        using var b = new SolidBrush(Pv.Orange);
        Pv.DrawTracked(g, t, Pv.Label, b, x, y, 2.0f);
    }

    /// <summary>Numero alinhado a DIREITA na coluna — senao a tabela nao lê.</summary>
    private static void Col(Graphics g, string s, int direita, int y, Brush b)
    {
        float w = Pv.TrackedWidth(g, s, Pv.Label, 1.0f);
        Pv.DrawTracked(g, s, Pv.Label, b, direita - w, y, 1.0f);
    }

    private static void Odd(Graphics g, double? odd, int direita, int y)
    {
        string s = odd == null ? "—" : odd.Value.ToString("0.00");
        using var b = new SolidBrush(odd == null ? Pv.Char3 : Pv.Orange);
        float w = Pv.TrackedWidth(g, s, Pv.Label, 1.0f);
        Pv.DrawTracked(g, s, Pv.Label, b, direita - w, y, 1.0f);
    }

    private static string Cortar(Graphics g, string s, float limite, Font f, float tr)
    {
        if (Pv.TrackedWidth(g, s, f, tr) <= limite) return s;
        while (s.Length > 4 && Pv.TrackedWidth(g, s + "...", f, tr) > limite) s = s[..^1];
        return s + "...";
    }
}
