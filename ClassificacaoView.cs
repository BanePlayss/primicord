namespace Primicord;

/// <summary>
/// A tabela do campeonato na coluna da direita.
/// </summary>
/// <remarks>
/// Desenhada num controle so, em vez de um controle por linha como o resto da UI
/// faz (RailItem, MemberRow). Com 8 times seriam 8 controles com handle, layout e
/// repintura propria pra mostrar texto que nunca recebe clique. Um OnPaint que
/// escreve 8 linhas custa menos e nao tem estado nenhum pra sincronizar.
/// </remarks>
public sealed class ClassificacaoView : Control
{
    private const int AlturaLinha = 20;
    private const int AlturaCabecalho = 18;

    private Classificacao? _tabela;

    /// <summary>Nick de quem esta logado, pra destacar a propria linha.</summary>
    public string MeuNick = "";

    /// <summary>Quantas posicoes mostrar. 0 = todas.</summary>
    public int MaxLinhas;

    /// <summary>
    /// As linhas que realmente vao pra tela: o topo, mais a MINHA linha quando ela
    /// fica de fora do corte.
    /// </summary>
    /// <remarks>
    /// Mostrar so o top 4 e util pra quem esta nele. Pra quem esta em setimo, um
    /// painel que nunca te menciona nao serve pra nada — entao a propria posicao
    /// entra sempre, destacada e fora de ordem, com o numero real do lado.
    /// </remarks>
    private List<(int Pos, TimeNaTabela Time)> Linhas()
    {
        var saida = new List<(int, TimeNaTabela)>();
        var t = _tabela;
        if (t == null) return saida;

        int corte = MaxLinhas > 0 ? Math.Min(MaxLinhas, t.Times.Count) : t.Times.Count;
        for (int i = 0; i < corte; i++) saida.Add((i + 1, t.Times[i]));

        if (MeuNick.Length > 0 && !saida.Any(x => x.Item2.Nick.Equals(MeuNick, StringComparison.OrdinalIgnoreCase)))
        {
            int meu = t.Times.FindIndex(x => x.Nick.Equals(MeuNick, StringComparison.OrdinalIgnoreCase));
            if (meu >= 0) saida.Add((meu + 1, t.Times[meu]));
        }
        return saida;
    }

    public ClassificacaoView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Pv.Char2;
        Height = 8 * AlturaLinha + AlturaCabecalho + 8;
    }

    public void Definir(Classificacao? tabela)
    {
        _tabela = tabela;
        Height = (Linhas().Count * AlturaLinha) + AlturaCabecalho + 8;
        Invalidate();
    }

    /// <summary>Linha de contexto pro cabecalho do painel ("rodada 5 de 7").</summary>
    public string Resumo => _tabela == null ? ""
        : _tabela.Situacao == "encerrado" ? "encerrado"
        : _tabela.Situacao == "nao comecou" ? "ainda nao comecou"
        : $"rodada {_tabela.RodadaAtual} de {_tabela.TotalRodadas}";

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using (var fundo = new SolidBrush(Pv.Char2)) g.FillRectangle(fundo, ClientRectangle);

        var t = _tabela;
        if (t == null || t.Vazia)
        {
            using var cinza = new SolidBrush(Pv.BoneDim);
            Pv.DrawTracked(g, "SEM TABELA", Pv.Label, cinza, 18, 6, 1.4f);
            return;
        }

        // Colunas medidas pra caber nos 212px do painel sem cortar nick.
        int xPos = 18, xNick = 34, xJ = Width - 74, xSg = Width - 52, xP = Width - 24;
        int y = 4;

        using (var cab = new SolidBrush(Pv.BoneDim))
        {
            Pv.DrawTracked(g, "J", Pv.Label, cab, xJ, y, 1.2f);
            Pv.DrawTracked(g, "SG", Pv.Label, cab, xSg, y, 1.2f);
            Pv.DrawTracked(g, "P", Pv.Label, cab, xP, y, 1.2f);
        }
        y += AlturaCabecalho;

        var linhas = Linhas();
        for (int li = 0; li < linhas.Count; li++)
        {
            var (pos, time) = linhas[li];
            int i = pos - 1;
            bool souEu = time.Nick.Equals(MeuNick, StringComparison.OrdinalIgnoreCase);

            if (souEu)
                using (var destaque = new SolidBrush(Pv.Char3))
                    g.FillRectangle(destaque, 8, y - 2, Width - 16, AlturaLinha);

            // Lider em laranja; o resto em osso. Quem esta logado sempre legivel.
            Color cor = i == 0 ? Pv.Orange : (souEu ? Pv.Bone : Pv.BoneDim);
            using var b = new SolidBrush(cor);
            using var bNum = new SolidBrush(i == 0 ? Pv.Orange : Pv.Bone);

            Pv.DrawTracked(g, pos.ToString(), Pv.Label, b, xPos, y, 1.0f);

            string nick = time.Nick.ToUpperInvariant();
            float largura = Pv.TrackedWidth(g, nick, Pv.Label, 1.0f);
            float limite = xJ - xNick - 6;
            if (largura > limite)
            {
                while (nick.Length > 3 && Pv.TrackedWidth(g, nick + "..", Pv.Label, 1.0f) > limite)
                    nick = nick[..^1];
                nick += "..";
            }
            Pv.DrawTracked(g, nick, Pv.Label, b, xNick, y, 1.0f);

            Pv.DrawTracked(g, time.J.ToString(), Pv.Label, b, xJ, y, 1.0f);
            string sg = time.Sg > 0 ? "+" + time.Sg : time.Sg.ToString();
            Pv.DrawTracked(g, sg, Pv.Label, b, xSg, y, 1.0f);
            Pv.DrawTracked(g, time.P.ToString(), Pv.Label, bNum, xP, y, 1.0f);

            y += AlturaLinha;
        }
    }
}
