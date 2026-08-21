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
    private readonly MessageCanvas _canvas;
    private readonly PrimInput _composer;
    private readonly Label _header = new();
    private readonly Label _subheader = new();

    public event Func<string, Task>? Send;

    public ChatView(bool compact = false)
    {
        BackColor = Pv.Charcoal;

        var head = new Panel
        {
            Dock = DockStyle.Top, Height = compact ? 44 : 56,
            BackColor = compact ? Pv.Char2 : Pv.Charcoal,
        };
        head.Paint += (_, e) =>
        {
            using var p = new Pen(Pv.Char3, 2);
            e.Graphics.DrawLine(p, 0, head.Height - 1, head.Width, head.Height - 1);
        };
        _header.Font = compact ? Pv.Label : Pv.DisplaySm;
        _header.ForeColor = compact ? Pv.Red : Pv.Bone;
        _header.AutoSize = true;
        _header.Location = compact ? new Point(14, 16) : new Point(20, 10);
        _subheader.Font = Pv.Body;
        _subheader.ForeColor = Pv.BoneDim;
        _subheader.AutoSize = true;
        _subheader.Location = new Point(20, 33);
        _subheader.Visible = !compact;
        head.Controls.AddRange(new Control[] { _header, _subheader });

        var bottom = new Panel
        {
            Dock = DockStyle.Bottom, Height = compact ? 56 : 62,
            BackColor = compact ? Pv.Char2 : Pv.Charcoal,
            Padding = compact ? new Padding(10, 8, 10, 10) : new Padding(16, 8, 16, 16),
        };
        _composer = new PrimInput("Manda a braba...") { Dock = DockStyle.Fill };
        async Task SendCurrentAsync()
        {
            string text = _composer.Value;
            if (text.Trim().Length == 0) return;
            _composer.Value = "";
            if (Send != null) await Send(text);
        }
        _composer.Box.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || e.Shift) return;
            e.SuppressKeyPress = true;
            await SendCurrentAsync();
        };
        PrimButton? sendButton = null;
        if (compact)
        {
            sendButton = new PrimButton("›", PrimButton.Style.Ghost)
            {
                Dock = DockStyle.Right, Width = 34, Height = 38,
                Margin = Padding.Empty,
            };
            sendButton.Click += async (_, _) => await SendCurrentAsync();
        }
        bottom.Controls.Add(_composer);
        if (sendButton != null) bottom.Controls.Add(sendButton);

        _canvas = new MessageCanvas(compact);
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

        private readonly int _avatarSize;
        private readonly int _leftPad;
        private readonly int _textLeft;
        private readonly int _groupGap;
        private const int GroupGapMs = 5 * 60 * 1000;

        public MessageCanvas(bool compact)
        {
            _avatarSize = compact ? 28 : 38;
            _leftPad = compact ? 12 : 20;
            _textLeft = _leftPad + _avatarSize + (compact ? 9 : 14);
            _groupGap = compact ? 6 : 10;
            AutoScroll = true;
            BackColor = compact ? Pv.Char2 : Pv.Charcoal;
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
            int wrapWidth = Math.Max(100, ClientSize.Width - _textLeft - 14);

            for (int i = 0; i < _msgs.Count; i++)
            {
                bool grouped = IsGrouped(i);
                if (!grouped && i > 0) y += _groupGap;
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
                g.DrawString("Ninguem falou nada ainda. Quebra o gelo.", Pv.Body, b, _leftPad, 20);
                return;
            }

            int y = 12 + AutoScrollPosition.Y;
            int wrapWidth = Math.Max(100, ClientSize.Width - _textLeft - 14);

            for (int i = 0; i < _msgs.Count; i++)
            {
                var m = _msgs[i];
                bool grouped = IsGrouped(i);
                if (!grouped && i > 0) y += _groupGap;

                var sz = g.MeasureString(m.Text, Pv.Body, wrapWidth);
                int blockH = (int)Math.Ceiling(sz.Height) + 4 + (grouped ? 0 : 22);

                // Fora da area visivel: so avanca (evita desenhar 60 mensagens sempre).
                if (y + blockH < 0) { y += blockH; continue; }
                if (y > ClientSize.Height) break;

                if (!grouped)
                {
                    var photo = Primitivao.AvatarFor(m.Nick);
                    var ac = new Rectangle(_leftPad, y, _avatarSize, _avatarSize);
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
                        using var f = new Font("Bahnschrift", Math.Max(11f, _avatarSize * .4f), FontStyle.Bold);
                        using var tb = new SolidBrush(Pv.Charcoal);
                        string ini = m.Nick.Length > 0 ? m.Nick[..1].ToUpperInvariant() : "?";
                        var isz = g.MeasureString(ini, f);
                        g.DrawString(ini, f, tb, ac.X + (_avatarSize - isz.Width) / 2,
                                     ac.Y + (_avatarSize - isz.Height) / 2);
                    }

                    bool isMe = string.Equals(m.Nick, _myNick, StringComparison.OrdinalIgnoreCase);
                    using (var b = new SolidBrush(isMe ? Pv.Orange : Pv.Bone))
                        g.DrawString(m.Nick, Pv.BodyBold, b, _textLeft, y - 2);

                    float nameW = g.MeasureString(m.Nick, Pv.BodyBold).Width;
                    using (var b = new SolidBrush(Pv.BoneDim))
                        g.DrawString(m.Local.ToString("HH:mm"), Pv.Label, b, _textLeft + nameW + 6, y + 3);

                    y += 22;
                }

                using (var b = new SolidBrush(Pv.Bone))
                    g.DrawString(m.Text, Pv.Body, b, new RectangleF(_textLeft, y, wrapWidth, sz.Height + 4));
                y += (int)Math.Ceiling(sz.Height) + 4;
            }
        }
    }
}
