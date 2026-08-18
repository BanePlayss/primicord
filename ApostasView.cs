namespace Primicord;

/// <summary>
/// Os confrontos abertos pra aposta, com as odds — o cartao de baixo da coluna
/// da direita.
/// </summary>
/// <remarks>
/// Duas linhas por confronto: os dois nomes em cima, as tres odds embaixo,
/// alinhadas com quem elas pagam. So exibe — apostar continua sendo no site,
/// que e quem tem o saldo e a trava de horario.
/// </remarks>
public sealed class ApostasView : Control
{
    private const int AlturaConfronto = 38;
    private const int AlturaCabecalho = 16;

    private List<ApostaLol> _apostas = new();

    public ApostasView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Pv.Char2;
        Height = AlturaCabecalho + 8;
    }

    public void Definir(List<ApostaLol> apostas)
    {
        _apostas = apostas;
        Height = apostas.Count == 0 ? 0 : (apostas.Count * AlturaConfronto) + AlturaCabecalho + 8;
        Visible = apostas.Count > 0;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using (var fundo = new SolidBrush(Pv.Char2)) g.FillRectangle(fundo, ClientRectangle);
        if (_apostas.Count == 0) return;

        int y = 4;
        int meio = Width / 2;

        foreach (var a in _apostas)
        {
            // Linha 1: CASA x FORA. O "x" no centro pra os nomes lerem como confronto.
            using (var b = new SolidBrush(Pv.Bone))
            using (var bx = new SolidBrush(Pv.BoneDim))
            {
                string casa = Curto(g, a.Casa.ToUpperInvariant(), meio - 26);
                string fora = Curto(g, a.Fora.ToUpperInvariant(), meio - 26);
                Pv.DrawTracked(g, casa, Pv.Label, b, 18, y, 1.0f);
                float wx = Pv.TrackedWidth(g, "x", Pv.Label, 1.0f);
                Pv.DrawTracked(g, "x", Pv.Label, bx, meio - wx / 2, y, 1.0f);
                float wf = Pv.TrackedWidth(g, fora, Pv.Label, 1.0f);
                Pv.DrawTracked(g, fora, Pv.Label, b, Width - 18 - wf, y, 1.0f);
            }

            // Linha 2: as tres odds, cada uma sob quem ela paga.
            y += 17;
            DesenhaOdd(g, a.OddCasa, 18, y, Alinhamento.Esquerda);
            DesenhaOdd(g, a.OddEmpate, meio, y, Alinhamento.Centro);
            DesenhaOdd(g, a.OddFora, Width - 18, y, Alinhamento.Direita);

            y += AlturaConfronto - 17;
        }
    }

    private enum Alinhamento { Esquerda, Centro, Direita }

    private static void DesenhaOdd(Graphics g, double? odd, float x, float y, Alinhamento al)
    {
        // Mercado nao oferecido (quase-certo) aparece como traco, e nao some: a
        // coluna vazia diria "esqueci de calcular".
        string texto = odd == null ? "—" : odd.Value.ToString("0.00");
        using var b = new SolidBrush(odd == null ? Pv.Char3 : Pv.Orange);
        float w = Pv.TrackedWidth(g, texto, Pv.Label, 1.0f);
        float px = al switch
        {
            Alinhamento.Esquerda => x,
            Alinhamento.Centro => x - w / 2,
            _ => x - w,
        };
        Pv.DrawTracked(g, texto, Pv.Label, b, px, y, 1.0f);
    }

    private static string Curto(Graphics g, string s, float limite)
    {
        if (Pv.TrackedWidth(g, s, Pv.Label, 1.0f) <= limite) return s;
        while (s.Length > 3 && Pv.TrackedWidth(g, s + "..", Pv.Label, 1.0f) > limite) s = s[..^1];
        return s + "..";
    }
}
