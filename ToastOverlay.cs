using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Primicord;

/// <summary>
/// Aviso flutuante no canto da tela — o retorno de "clipe salvo" quando o atalho e
/// apertado com o jogo em primeiro plano e o Primicord invisivel.
/// </summary>
/// <remarks>
/// Sempre-no-topo e ATRAVESSAVEL pelo mouse (WS_EX_TRANSPARENT): nunca rouba clique
/// nem foco do jogo. Nao aparece por cima de jogo em tela cheia exclusiva — por isso
/// o aviso vem sempre acompanhado de um som, que toca de qualquer jeito.
/// </remarks>
public sealed class ToastOverlay : Form
{
    private const int WsExTransparent = 0x20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x80;

    private readonly System.Windows.Forms.Timer _life = new();
    private string _text = "";
    private string _sub = "";
    private int _ticks;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExTransparent | WsExNoActivate | WsExToolWindow;
            return cp;
        }
    }

    public ToastOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Pv.Charcoal;
        Size = new Size(340, 74);
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);

        _life.Interval = 100;
        _life.Tick += (_, _) =>
        {
            _ticks++;
            if (_ticks > 26) { Hide(); _life.Stop(); return; }
            if (_ticks > 20) Opacity = Math.Max(0, 1.0 - (_ticks - 20) / 6.0);
        };
    }

    /// <summary>Mostra no canto inferior direito do monitor principal.</summary>
    public void Show(string text, string sub)
    {
        _text = text;
        _sub = sub;
        _ticks = 0;
        Opacity = 1;

        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Right - Width - 24, wa.Bottom - Height - 24);

        Invalidate();
        if (!Visible) Show();
        else BringToFront();
        _life.Stop();
        _life.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var b = new SolidBrush(Pv.Char2)) g.FillRectangle(b, r);
        using (var p = new Pen(Pv.Orange, 2)) g.DrawRectangle(p, r);
        using (var b = new SolidBrush(Pv.Orange)) g.FillRectangle(b, 0, 0, 5, Height);

        using (var b = new SolidBrush(Pv.Bone))
            Pv.DrawTracked(g, _text, Pv.DisplaySm, b, 20, 14, 1.4f);
        using (var b = new SolidBrush(Pv.BoneDim))
            g.DrawString(_sub, Pv.Body, b, 21, 42);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _life.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Bipes curtos gerados na hora — confirmam o clipe mesmo em tela cheia.</summary>
public static class Chime
{
    [DllImport("kernel32.dll")] private static extern bool Beep(uint freq, uint duration);

    /// <summary>Dois tons subindo: deu certo.</summary>
    public static void Ok() => Task.Run(() =>
    {
        try { Beep(880, 90); Beep(1320, 120); } catch { }
    });

    /// <summary>Tom grave: nao deu.</summary>
    public static void Fail() => Task.Run(() =>
    {
        try { Beep(300, 180); } catch { }
    });
}
