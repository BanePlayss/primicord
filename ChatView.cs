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
    private readonly TextBox _search = new();

    public event Func<string, Task>? Send;
    public event Action? ToggleMembers;

    public ChatView()
    {
        BackColor = Pv.Charcoal;

        var head = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Pv.Charcoal };
        head.Paint += (_, e) =>
        {
            using var p = new Pen(Pv.Border, 1);
            e.Graphics.DrawLine(p, 0, head.Height - 1, head.Width, head.Height - 1);
        };
        _header.Font = Pv.BodyBold;
        _header.ForeColor = Pv.Bone;
        _header.AutoSize = true;
        _header.Location = new Point(18, 18);
        _subheader.Font = Pv.Body;
        _subheader.ForeColor = Pv.BoneDim;
        _subheader.AutoSize = true;
        _subheader.Location = new Point(100, 18);
        head.Controls.AddRange(new Control[] { _header, _subheader });

        var members = new GlyphButton(Glyphs.Users) { Size = new Size(34, 34) };
        members.ToolTipText = "Mostrar ou ocultar membros";
        members.Click += (_, _) => ToggleMembers?.Invoke();

        var pins = new GlyphButton(Glyphs.Pin) { Size = new Size(34, 34) };
        pins.ToolTipText = "Mensagens fixadas";
        pins.Click += (_, _) => MessageBox.Show("Este canal ainda nao tem mensagens fixadas.",
            "Mensagens fixadas", MessageBoxButtons.OK, MessageBoxIcon.Information);

        bool notificationsMuted = false;
        var bell = new GlyphButton(Glyphs.Bell) { Size = new Size(34, 34) };
        bell.ToolTipText = "Silenciar notificacoes do canal";
        bell.Click += (_, _) =>
        {
            notificationsMuted = !notificationsMuted;
            bell.Accent = notificationsMuted ? Pv.Red : Pv.Bone;
            bell.ToolTipText = notificationsMuted ? "Ativar notificacoes" : "Silenciar notificacoes do canal";
            bell.Invalidate();
        };

        var searchPanel = new Panel { Size = new Size(174, 30), BackColor = Pv.SurfaceLowest };
        _search.BorderStyle = BorderStyle.None;
        _search.BackColor = Pv.SurfaceLowest;
        _search.ForeColor = Pv.Bone;
        _search.Font = Pv.Body;
        _search.PlaceholderText = "Buscar";
        _search.Location = new Point(9, 7);
        _search.Width = 136;
        _search.TextChanged += (_, _) => _canvas.SetFilter(_search.Text);
        var searchGlyph = new GlyphButton(Glyphs.Search)
        {
            Size = new Size(28, 28), Location = new Point(145, 1), Enabled = false,
        };
        searchPanel.Controls.AddRange(new Control[] { _search, searchGlyph });

        void LayoutHeader()
        {
            members.Location = new Point(head.ClientSize.Width - 42, 11);
            searchPanel.Location = new Point(members.Left - searchPanel.Width - 8, 13);
            pins.Location = new Point(searchPanel.Left - 38, 11);
            bell.Location = new Point(pins.Left - 36, 11);
            bool compact = bell.Left < 250;
            bell.Visible = !compact;
            pins.Visible = !compact;
            searchPanel.Visible = searchPanel.Left > 150;
        }
        head.Resize += (_, _) => LayoutHeader();
        head.Controls.AddRange(new Control[] { bell, pins, searchPanel, members });

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 70, BackColor = Pv.Charcoal,
                                 Padding = new Padding(16, 8, 16, 18) };
        _composer = new PrimInput("Conversar no canal") { Dock = DockStyle.Fill };
        _composer.Box.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || e.Shift) return;
            e.SuppressKeyPress = true;
            string text = _composer.Value;
            if (text.Trim().Length == 0) return;
            _composer.Value = "";
            if (Send != null) await Send(text);
        };

        var emoji = new GlyphButton(Glyphs.Smile)
        {
            Dock = DockStyle.Right, Width = 38, Accent = Pv.BoneDim,
        };
        emoji.ToolTipText = "Escolher emoji";
        emoji.Click += (_, _) => ShowEmojiPicker(emoji);

        var composerShell = new Panel { Dock = DockStyle.Fill, BackColor = Pv.Input, Padding = new Padding(4, 0, 4, 0) };
        composerShell.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var p = new Pen(Pv.Input, 1);
            using var path = Pv.RoundRect(new Rectangle(0, 0, composerShell.Width - 1, composerShell.Height - 1), 8);
            e.Graphics.DrawPath(p, path);
        };
        composerShell.Controls.Add(_composer);
        composerShell.Controls.Add(emoji);
        bottom.Controls.Add(composerShell);

        _canvas.Dock = DockStyle.Fill;

        Controls.Add(_canvas);
        Controls.Add(bottom);
        Controls.Add(head);
        LayoutHeader();
    }

    private void ShowEmojiPicker(Control anchor)
    {
        var menu = new ContextMenuStrip { BackColor = Pv.SurfaceLow, ForeColor = Pv.Bone };
        foreach (string emoji in new[] { "😀", "😂", "😍", "🔥", "💜", "👍", "🎮", "🚀", "✨", "😎" })
        {
            string value = emoji;
            menu.Items.Add(value, null, (_, _) =>
            {
                int at = _composer.Box.SelectionStart;
                _composer.Box.Text = _composer.Box.Text.Insert(at, value);
                _composer.Box.SelectionStart = at + value.Length;
                _composer.Box.Focus();
            });
        }
        menu.Show(anchor, new Point(anchor.Width, 0), ToolStripDropDownDirection.AboveLeft);
    }

    public void SetHeader(string title, string subtitle)
    {
        if (_search.TextLength > 0) _search.Clear();
        _header.Text = title;
        _subheader.Text = subtitle;
        _subheader.Left = _header.Right + 16;
        _composer.Box.PlaceholderText = title.StartsWith("#")
            ? $"Conversar em {title}"
            : "Mensagem para " + title.Replace("@ ", "@");
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
        private List<ChatMessage> _allMessages = new();
        private string _myNick = "";
        private string _filter = "";
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
            _allMessages = msgs;
            _msgs = ApplyFilter(msgs);
            _myNick = myNick;
            if (!changed) return;

            // "Colado embaixo" = usuario esta lendo o fim; ai seguimos rolando junto.
            _stickToBottom = VerticalScroll.Value >= VerticalScroll.Maximum - ClientSize.Height - 40
                             || VerticalScroll.Maximum <= ClientSize.Height;
            Relayout();
            Invalidate();
        }

        public void SetFilter(string query)
        {
            string normalized = query.Trim();
            if (string.Equals(_filter, normalized, StringComparison.OrdinalIgnoreCase)) return;
            _filter = normalized;
            _msgs = ApplyFilter(_allMessages);
            _stickToBottom = false;
            Relayout();
            Invalidate();
        }

        private List<ChatMessage> ApplyFilter(List<ChatMessage> source)
            => _filter.Length == 0
                ? source
                : source.Where(m => m.Text.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                                  || m.Nick.Contains(_filter, StringComparison.OrdinalIgnoreCase)).ToList();

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
                string empty = _filter.Length > 0
                    ? $"Nenhuma mensagem encontrada para \"{_filter}\"."
                    : "Ninguem falou nada ainda. Quebra o gelo.";
                g.DrawString(empty, Pv.Body, b, LeftPad, 20);
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
                        g.DrawString(m.Nick, Pv.BodyBold, b, TextLeft, y - 2);

                    float nameW = g.MeasureString(m.Nick, Pv.BodyBold).Width;
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
