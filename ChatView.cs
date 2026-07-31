using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Primicord;

/// <summary>
/// A conversa: lista de mensagens + campo de escrever.
/// </summary>
/// <remarks>
/// Desenhado num canvas so (nao um controle por mensagem) porque com 60 mensagens
/// o WinForms engasga criando/destruindo controle a cada atualizacao do poll.
/// Mensagens seguidas da mesma pessoa em ate 5min sao agrupadas: so a primeira
/// mostra foto e nome, como no Discord.
/// </remarks>
public sealed class ChatView : Panel
{
    private readonly MessageCanvas _canvas = new();
    private readonly PrimInput _composer;
    private readonly Label _header = new();
    private readonly Label _subheader = new();

    public event Func<string, Task>? Send;

    public ChatView()
    {
        BackColor = Pv.Charcoal;

        var head = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Pv.Charcoal };
        head.Paint += (_, e) =>
        {
            using var p = new Pen(Pv.Char3, 2);
            e.Graphics.DrawLine(p, 0, head.Height - 1, head.Width, head.Height - 1);
        };
        _header.Font = Pv.DisplaySm;
        _header.ForeColor = Pv.Bone;
        _header.AutoSize = true;
        _header.Location = new Point(20, 10);
        _subheader.Font = Pv.Body;
        _subheader.ForeColor = Pv.BoneDim;
        _subheader.AutoSize = true;
        _subheader.Location = new Point(20, 33);
        head.Controls.AddRange(new Control[] { _header, _subheader });

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 62, BackColor = Pv.Charcoal,
                                 Padding = new Padding(16, 8, 16, 16) };
        _composer = new PrimInput("Manda a braba...") { Dock = DockStyle.Fill };
        _composer.Box.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || e.Shift) return;
            e.SuppressKeyPress = true;
            string text = _composer.Value;
            if (text.Trim().Length == 0) return;
            _composer.Value = "";
            if (Send != null) await Send(text);
        };
        bottom.Controls.Add(_composer);

        _canvas.Dock = DockStyle.Fill;

        Controls.Add(_canvas);
        Controls.Add(bottom);
        Controls.Add(head);
    }

    public void SetHeader(string title, string subtitle)
    {
        _header.Text = title;
        _subheader.Text = subtitle;
    }

    public void SetMessages(List<ChatMessage> msgs, string myNick) => _canvas.SetMessages(msgs, myNick);

    public void FocusComposer() => _composer.Box.Focus();

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string ComposerPlaceholder
    {
        get => _composer.Box.PlaceholderText;
        set => _composer.Box.PlaceholderText = value;
    }

    /// <summary>Canvas rolavel que pinta todas as mensagens.</summary>
    private sealed class MessageCanvas : Panel
    {
        private List<ChatMessage> _msgs = new();
        private string _myNick = "";
        private bool _stickToBottom = true;
        private string _lastSignature = "";

        private const int AvatarSize = 38;
        private const int LeftPad = 20;
        private const int TextLeft = LeftPad + AvatarSize + 14;
        private const int GroupGapMs = 5 * 60 * 1000;

        public MessageCanvas()
        {
            AutoScroll = true;
            BackColor = Pv.Charcoal;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }

        public void SetMessages(List<ChatMessage> msgs, string myNick)
        {
            // So relayouta se mudou de verdade — o poll roda a cada 2s.
            string sig = msgs.Count + "|" + (msgs.Count > 0 ? msgs[^1].Id : "");
            bool changed = sig != _lastSignature;
            _lastSignature = sig;
            _msgs = msgs;
            _myNick = myNick;
            if (!changed) return;

            // "Colado embaixo" = usuario esta lendo o fim; ai seguimos rolando junto.
            _stickToBottom = VerticalScroll.Value >= VerticalScroll.Maximum - ClientSize.Height - 40
                             || VerticalScroll.Maximum <= ClientSize.Height;
            Relayout();
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
            Invalidate();
        }

        private int _contentHeight;

        private void Relayout()
        {
            using var g = CreateGraphics();
            int y = 12;
            int wrapWidth = Math.Max(120, ClientSize.Width - TextLeft - 24);

            for (int i = 0; i < _msgs.Count; i++)
            {
                bool grouped = IsGrouped(i);
                if (!grouped && i > 0) y += 10;
                if (!grouped) y += 22;                      // linha do nome
                var sz = g.MeasureString(_msgs[i].Text, Pv.Body, wrapWidth);
                y += (int)Math.Ceiling(sz.Height) + 4;
            }
            _contentHeight = y + 12;
            AutoScrollMinSize = new Size(0, _contentHeight);

            if (_stickToBottom)
            {
                int max = Math.Max(0, _contentHeight - ClientSize.Height);
                AutoScrollPosition = new Point(0, max);
            }
        }

        private bool IsGrouped(int i)
        {
            if (i == 0) return false;
            var prev = _msgs[i - 1];
            var cur = _msgs[i];
            return string.Equals(prev.Nick, cur.Nick, StringComparison.OrdinalIgnoreCase)
                   && cur.At - prev.At < GroupGapMs;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (_msgs.Count == 0)
            {
                using var b = new SolidBrush(Pv.BoneDim);
                g.DrawString("Ninguem falou nada ainda. Quebra o gelo.", Pv.Body, b, LeftPad, 20);
                return;
            }

            int y = 12 + AutoScrollPosition.Y;
            int wrapWidth = Math.Max(120, ClientSize.Width - TextLeft - 24);

            for (int i = 0; i < _msgs.Count; i++)
            {
                var m = _msgs[i];
                bool grouped = IsGrouped(i);
                if (!grouped && i > 0) y += 10;

                var sz = g.MeasureString(m.Text, Pv.Body, wrapWidth);
                int blockH = (int)Math.Ceiling(sz.Height) + 4 + (grouped ? 0 : 22);

                // Fora da area visivel: so avanca (evita desenhar 60 mensagens sempre).
                if (y + blockH < 0) { y += blockH; continue; }
                if (y > ClientSize.Height) break;

                if (!grouped)
                {
                    var photo = Primitivao.AvatarFor(m.Nick);
                    var ac = new Rectangle(LeftPad, y, AvatarSize, AvatarSize);
                    if (photo != null)
                    {
                        using var clip = new GraphicsPath();
                        clip.AddEllipse(ac);
                        var saved = g.Save();
                        g.SetClip(clip);
                        int side = Math.Min(photo.Width, photo.Height);
                        g.DrawImage(photo, ac,
                            new Rectangle((photo.Width - side) / 2, (photo.Height - side) / 2, side, side),
                            GraphicsUnit.Pixel);
                        g.Restore(saved);
                    }
                    else
                    {
                        using var b = new SolidBrush(Pv.Orange);
                        g.FillEllipse(b, ac);
                        using var f = new Font("Bahnschrift", 15f, FontStyle.Bold);
                        using var tb = new SolidBrush(Pv.Charcoal);
                        string ini = m.Nick.Length > 0 ? m.Nick[..1].ToUpperInvariant() : "?";
                        var isz = g.MeasureString(ini, f);
                        g.DrawString(ini, f, tb, ac.X + (AvatarSize - isz.Width) / 2,
                                     ac.Y + (AvatarSize - isz.Height) / 2);
                    }

                    bool isMe = string.Equals(m.Nick, _myNick, StringComparison.OrdinalIgnoreCase);
                    using (var b = new SolidBrush(isMe ? Pv.Orange : Pv.Bone))
                        g.DrawString(m.Nick.ToUpperInvariant(), Pv.BodyBold, b, TextLeft, y - 2);

                    float nameW = g.MeasureString(m.Nick.ToUpperInvariant(), Pv.BodyBold).Width;
                    using (var b = new SolidBrush(Pv.BoneDim))
                        g.DrawString(m.Local.ToString("HH:mm"), Pv.Label, b, TextLeft + nameW + 6, y + 3);

                    y += 22;
                }

                using (var b = new SolidBrush(Pv.Bone))
                    g.DrawString(m.Text, Pv.Body, b, new RectangleF(TextLeft, y, wrapWidth, sz.Height + 4));
                y += (int)Math.Ceiling(sz.Height) + 4;
            }
        }
    }
}
