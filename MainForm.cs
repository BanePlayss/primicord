using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Primicord;

/// <summary>
/// Shell do app: canais a esquerda, conversa no
/// meio, membros a direita, painel do usuario (com a engrenagem) no rodape do rail.
/// </summary>
/// <remarks>
/// Layout e todo por DOCK + reposicionamento no Resize, nunca por coordenada fixa
/// calculada do ClientSize no construtor — o Windows escala por DPI e tamanho fixo
/// estoura pra fora da tela.
/// </remarks>
public sealed class MainForm : Form
{
    private readonly bool _preview;
    private readonly Firestore _fs = new();
    private readonly RoomDirectory _dir;
    private readonly Config _cfg;

    private PrimitivaoUser? _me;
    private ChatService? _chat;
    private string Nick => _me?.Nick ?? _cfg.Nick;

    // voz
    private RoomSession? _session;
    private VoiceEngine? _voice;
    private string _voiceRoomId = "";
    private string _voiceRoomName = "";

    // tela, clipe e DJ
    private ScreenSender? _screenSender;
    private readonly ScreenReceiver _screens = new();
    private ClipRecorder? _clips;
    private MusicShare? _music;
    private StageView? _stage;
    private uint _focusedSharer;          // 0 = minha propria tela
    private bool _iAmSharing;
    private long _nextPeerClipAt;
    private Label? _djLabel;
    private string _nowPlaying = "";

    private readonly HotkeyBinding _clipHotkey = new(1);
    private ToastOverlay? _toast;

    // shell
    private readonly Panel _body = new() { Dock = DockStyle.Fill, BackColor = Pv.Charcoal };
    private readonly Label _banner = new()
    {
        Dock = DockStyle.Top, Height = 0, BackColor = Pv.Red, ForeColor = Pv.Bone,
        TextAlign = ContentAlignment.MiddleCenter, Font = Pv.Body, Visible = false,
    };

    private Panel? _railList, _voiceStrip, _userPanel, _membersList, _membersPanel, _contentHost;
    private ChatView? _chatView;
    private DjView? _djView;
    private DashboardView? _dashboard;
    private Panel? _roomPanel;
    private FlowLayoutPanel? _tiles;
    private readonly Dictionary<uint, PeerTile> _peerTiles = new();
    private PeerTile? _myTile;
    private Label? _roomStatus;
    private bool _deafened;
    private bool _mutedBeforeDeafen;

    // estado da navegacao: "geral" | "dm:<nick>" | "room:<id>"
    private string _view = "geral";
    private static readonly (string Id, string Name, string Description)[] TextChannels =
    {
        (ChatService.GeneralChannel, "geral", "Conversa principal da comunidade."),
        ("clipes", "clipes", "Jogadas, cortes e momentos que merecem replay."),
        ("musica", "musica", "Faixas, playlists e combinados do DJ."),
        ("off-topic", "off-topic", "O papo que nao cabe nos outros canais."),
    };
    private List<RoomInfo> _rooms = new();
    private List<string> _members = new();
    private Dictionary<string, MemberPresence> _presence = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _openDms = new();

    // O cadastro de servidores é local e explícito: não fazemos varredura da
    // rede nem inventamos servidores. A aba só anuncia endpoints que o usuário
    // adicionou e mostra o resultado de uma sondagem real.
    private readonly GameServerDirectory _gameServers = new();
    private readonly GameServerProbe _gameProbe = new();
    private Dictionary<string, GameServerStatus> _serverStatuses = new();
    private GameServersPanel? _serverPanel;

    private System.Windows.Forms.Timer? _pollTimer, _voiceTimer;
    private System.Windows.Forms.Timer? _stageTimer;
    private int _pollTick;
    private bool _polling;

    public MainForm(bool preview = false)
    {
        _preview = preview;
        _dir = new RoomDirectory(_fs);
        _cfg = Config.Load();

        Text = "PRIMICORD";
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe != null) Icon = Icon.ExtractAssociatedIcon(exe);
        }
        catch (Exception ex) { Log.Write("icone da janela: " + ex.Message); }

        ClientSize = new Size(1280, 760);
        MinimumSize = new Size(960, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Pv.Charcoal;
        ForeColor = Pv.Bone;
        Font = Pv.Body;
        DoubleBuffered = true;

        Controls.Add(_body);
        Controls.Add(_banner);

        if (_preview)
        {
            // Dados visuais isolados: nenhum login, Firestore, áudio ou presença é
            // iniciado neste modo. Isso permite validar o shell em qualquer PC.
            OnLoggedIn(new PrimitivaoUser { Nick = "bane", Pc = 12500, ThemeId = "breu" });
        }
        else ShowLogin();
        if (!string.IsNullOrWhiteSpace(_cfg.Nick) && !string.IsNullOrWhiteSpace(_cfg.SenhaHash))
            _ = TryAutoLoginAsync();
    }

    private void SetBody(Control c)
    {
        _body.SuspendLayout();
        foreach (Control old in _body.Controls.Cast<Control>().ToList()) old.Dispose();
        _body.Controls.Clear();
        c.Dock = DockStyle.Fill;
        _body.Controls.Add(c);
        _body.ResumeLayout();
        Invalidate(true);
    }

    private void ShowBanner(string msg)
    {
        if (InvokeRequired) { BeginInvoke(() => ShowBanner(msg)); return; }
        _banner.Text = msg;
        _banner.Height = 42;
        _banner.Visible = true;
    }

    private static Label SectionLabel(string text) => new()
    {
        Text = text, Font = Pv.Label, ForeColor = Pv.BoneDim, AutoSize = true,
    };

    // ═══════════════════════════════════════════════════════════════════════
    // LOGIN
    // ═══════════════════════════════════════════════════════════════════════

    private PrimInput? _loginNick, _loginPass;
    private Label? _loginErr;
    private PrimButton? _loginBtn;

    private void ShowLogin(string? presetNick = null)
    {
        _me = null;
        StopTimers();

        var host = new Panel { BackColor = Pv.SurfaceLowest };
        host.Paint += (_, e) =>
        {
            e.Graphics.Clear(Pv.SurfaceLowest);
            using var glow = new LinearGradientBrush(host.ClientRectangle,
                Color.FromArgb(36, Pv.OrangeDim), Pv.SurfaceLowest, 35f);
            e.Graphics.FillRectangle(glow, host.ClientRectangle);
        };
        var card = new Panel { Size = new Size(440, 430), BackColor = Pv.Char2 };
        card.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var p = new Pen(Pv.Border, 1)) g.DrawRectangle(p, 0, 0, card.Width - 1, card.Height - 1);
            using (var b = new SolidBrush(Pv.Orange))
                g.FillRectangle(b, 0, 0, card.Width, 4);
            BrandAssets.Draw(g, new Rectangle((card.Width - 106) / 2, 12, 106, 96));
            using (var b = new SolidBrush(Pv.Bone))
            {
                string title = "Bem-vindo ao acampamento";
                var size = g.MeasureString(title, Pv.DisplaySm);
                g.DrawString(title, Pv.DisplaySm, b, (card.Width - size.Width) / 2f, 116);
            }
            using (var b = new SolidBrush(Pv.Orange))
                Pv.DrawTracked(g, "PRIMITIVÃO · VOZ / TELA / DJ", Pv.Label, b, 78, 143, 1.2f);
        };

        var hint = new Label
        {
            Text = "Entre com a mesma conta do site do Primitivão.",
            Font = Pv.Body, ForeColor = Pv.BoneDim, Location = new Point(66, 164), AutoSize = true,
        };

        var lblNick = SectionLabel("NICK");
        lblNick.Location = new Point(24, 194);
        _loginNick = new PrimInput("seu nick")
        {
            Location = new Point(24, 216), Width = 392,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        _loginNick.Value = presetNick ?? _cfg.Nick;

        var lblPass = SectionLabel("SENHA");
        lblPass.Location = new Point(24, 272);
        _loginPass = new PrimInput("sua senha")
        {
            Location = new Point(24, 294), Width = 392,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        _loginPass.Box.UseSystemPasswordChar = true;

        _loginErr = new Label
        {
            Text = "", Font = Pv.Body, ForeColor = Pv.Red, Location = new Point(24, 342),
            Size = new Size(392, 44), AutoSize = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        _loginBtn = new PrimButton("ENTRAR")
        {
            Location = new Point(24, 354), Size = new Size(392, 44),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        _loginBtn.Click += async (_, _) => await DoLoginAsync();
        _loginNick.Box.KeyDown += (_, e) =>
        { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _loginPass!.Box.Focus(); } };
        _loginPass.Box.KeyDown += async (_, e) =>
        { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await DoLoginAsync(); } };

        card.Controls.AddRange(new Control[]
            { hint, lblNick, _loginNick, lblPass, _loginPass, _loginErr, _loginBtn });
        host.Controls.Add(card);

        void Center() => card.Location = new Point(
            Math.Max(0, (host.ClientSize.Width - card.Width) / 2),
            Math.Max(10, (host.ClientSize.Height - card.Height) / 2 - 20));
        host.Resize += (_, _) => Center();

        SetBody(host);
        Center();
        if (_loginNick.Value.Length > 0) _loginPass.Box.Focus(); else _loginNick.Box.Focus();
    }

    private async Task DoLoginAsync()
    {
        if (_loginNick == null || _loginPass == null) return;
        SetLoginBusy(true, "");
        var res = await Primitivao.AuthenticateAsync(_fs, _loginNick.Value, _loginPass.Value);
        if (!res.Ok) { SetLoginBusy(false, res.Error ?? "Nao consegui entrar"); return; }
        OnLoggedIn(res.User!);
    }

    private async Task TryAutoLoginAsync()
    {
        SetLoginBusy(true, "");
        var res = await Primitivao.AuthenticateWithHashAsync(_fs, _cfg.Nick, _cfg.SenhaHash);
        if (res.Ok) { OnLoggedIn(res.User!); return; }
        Log.Write("auto-login falhou: " + res.Error);
        SetLoginBusy(false, res.Error ?? "");
    }

    private void OnLoggedIn(PrimitivaoUser user)
    {
        if (InvokeRequired) { BeginInvoke(() => OnLoggedIn(user)); return; }
        _me = user;
        if (!_preview)
        {
            _cfg.Nick = user.Nick;
            _cfg.SenhaHash = user.SenhaHash;
            _cfg.Save();
        }
        _chat = new ChatService(_fs, user.Nick);

        // Adota o tema escolhido no site (so leitura — trocar continua sendo la).
        Pv.SetAccent(!_preview && _cfg.UseSiteTheme ? user.ThemeAccent : null);
        if (!_preview && _cfg.UseSiteTheme && user.ThemeAccent != null)
            Log.Write($"tema do site aplicado: {user.ThemeId}");

        if (_preview)
        {
            _rooms = new List<RoomInfo>
            {
                new() { Id = "arena", Name = "ARENA PRINCIPAL", CreatedBy = "bane" },
                new() { Id = "ranqueada", Name = "RANQUEADA", CreatedBy = "mohamed" },
            };
            _rooms[0].Occupants.AddRange(new[] { "bane", "mohamed", "ricle", "vitinho", "angu" });
            _rooms[0].LiveStreams = 1;
            _members = new List<string> { "bane", "mohamed", "ricle", "vitinho", "angu", "jessica", "pedro" };
            long previewNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _presence = _members.ToDictionary(n => n,
                n => new MemberPresence { LastSeen = n is "bane" or "mohamed" or "ricle" or "vitinho" or "angu"
                    ? previewNow : 0, Room = n is "bane" or "mohamed" or "ricle" or "vitinho" or "angu"
                    ? "ARENA PRINCIPAL" : "" },
                StringComparer.OrdinalIgnoreCase);
        }
        BuildShell();
    }

    private void SetLoginBusy(bool busy, string error)
    {
        if (InvokeRequired) { BeginInvoke(() => SetLoginBusy(busy, error)); return; }
        if (_loginBtn == null || _loginBtn.IsDisposed) return;
        _loginBtn.Enabled = !busy;
        _loginBtn.Text = busy ? "ENTRANDO..." : "ENTRAR";
        _loginBtn.Invalidate();
        if (_loginErr != null && !_loginErr.IsDisposed) _loginErr.Text = error;
    }

    private void Logout()
    {
        LeaveVoice();
        _ = _chat?.ClearPresenceAsync();
        _cfg.SenhaHash = "";
        _cfg.Save();
        ShowLogin(_cfg.Nick);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SHELL
    // ═══════════════════════════════════════════════════════════════════════

    private void BuildShell()
    {
        var host = new Panel { BackColor = Pv.Charcoal };

        // ── CANAIS DA COMUNIDADE UNICA ──
        var rail = new Panel { Dock = DockStyle.Left, Width = 240, BackColor = Pv.Char2 };
        rail.Paint += (_, e) =>
        {
            using var p = new Pen(Pv.Border, 1);
            e.Graphics.DrawLine(p, rail.Width - 1, 0, rail.Width - 1, rail.Height);
        };

        var brand = new Panel { Dock = DockStyle.Top, Height = 70, BackColor = Pv.Char2, Cursor = Cursors.Hand };
        brand.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            BrandAssets.Draw(g, new Rectangle(12, 8, 49, 48));
            using (var b = new SolidBrush(Pv.Bone))
                g.DrawString("PRIMITIVÃO", Pv.DisplaySm, b, 69, 13);
            using (var b = new SolidBrush(Pv.Orange))
                Pv.DrawTracked(g, "ACAMPAMENTO", Pv.Label, b, 71, 39, 1.3f);
            using (var p = new Pen(Pv.Border, 1))
                g.DrawLine(p, 0, brand.Height - 1, brand.Width, brand.Height - 1);
        };
        brand.Click += (_, _) => SelectView("home");

        var nitro = BuildNitroBanner();

        _userPanel = BuildUserPanel();
        _voiceStrip = BuildVoiceStrip();
        _railList = new Panel
        {
            Dock = DockStyle.Fill, AutoScroll = true, BackColor = Pv.Char2,
            Padding = new Padding(0, 8, 0, 8),
        };

        rail.Controls.Add(_railList);   // Fill primeiro
        rail.Controls.Add(_voiceStrip);
        rail.Controls.Add(_userPanel);
        rail.Controls.Add(nitro);
        rail.Controls.Add(brand);

        // ── MEMBROS (direita) ──
        var members = new Panel { Dock = DockStyle.Right, Width = 232, BackColor = Pv.Char2 };
        _membersPanel = members;
        members.Paint += (_, e) =>
        {
            using var p = new Pen(Pv.Border, 1);
            e.Graphics.DrawLine(p, 0, 0, 0, members.Height);
        };
        var mHead = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Pv.Char2 };
        mHead.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using var b = new SolidBrush(Pv.Bone);
            g.DrawString("Membros", Pv.BodyBold, b, 18, 18);
        };
        _membersList = new Panel
        {
            Dock = DockStyle.Fill, AutoScroll = true, BackColor = Pv.Char2,
            Padding = new Padding(0, 4, 0, 8),
        };
        members.Controls.Add(_membersList);
        members.Controls.Add(mHead);

        // ── CONTEUDO ──
        _contentHost = new Panel { Dock = DockStyle.Fill, BackColor = Pv.Charcoal };

        host.Controls.Add(_contentHost);   // Fill primeiro
        host.Controls.Add(members);
        host.Controls.Add(rail);
        SetBody(host);

        _chatView = new ChatView { Dock = DockStyle.Fill };
        _chatView.Send += OnSendMessageAsync;
        _chatView.ToggleMembers += ToggleMembersPanel;

        SelectView(_preview ? "home" : "geral");
        RebuildRail();

        if (!_preview)
        {
            _pollTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _pollTimer.Tick += async (_, _) => await PollAsync();
            _pollTimer.Start();
            _ = PollAsync();
        }
        else
        {
            ShowBanner("PRÉVIA LOCAL · Firestore, voz e Tailscale desligados");
        }
    }

    private Panel BuildNitroBanner()
    {
        var p = new Panel
        {
            Dock = DockStyle.Top, Height = 84, BackColor = Pv.Char2,
            Padding = new Padding(10, 8, 10, 8),
        };
        p.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(10, 8, Math.Max(1, p.Width - 20), 68);
            using (var b = new SolidBrush(Pv.SurfaceLow))
            using (var path = Pv.RoundRect(r, 10)) g.FillPath(b, path);
            using (var pen = new Pen(Pv.OrangeDim, 1))
            using (var path = Pv.RoundRect(r, 10)) g.DrawPath(pen, path);
            using (var b = new SolidBrush(Pv.Orange))
                Pv.DrawTracked(g, "REDE DA TRIBO", Pv.Label, b, r.X + 12, r.Y + 11, 1.2f);
            string status = _preview ? "Prévia isolada" : ConnectionPolicy.Summary;
            using (var b = new SolidBrush(Pv.Bone))
                g.DrawString("Tailscale · " + status, Pv.BodyBold, b, r.X + 12, r.Y + 31);
            using (var b = new SolidBrush(Pv.BoneDim))
                g.DrawString("voz, tela e DJ no mesmo acampamento", Pv.Label, b, r.X + 12, r.Y + 50);
            using (var b = new SolidBrush(_preview || ConnectionPolicy.TailnetAddresses().Count > 0 ? Pv.Green : Pv.Red))
                g.FillEllipse(b, r.Right - 22, r.Y + 28, 8, 8);
        };
        return p;
    }

    private void ToggleMembersPanel()
    {
        if (_membersPanel == null) return;
        _membersPanel.Visible = !_membersPanel.Visible;
    }

    private Panel BuildUserPanel()
    {
        var p = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Pv.SurfaceLow };
        p.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            if (_me == null) return;

            var box = new Rectangle(8, (p.Height - 34) / 2, 34, 34);
            Glyphs.Avatar(g, box, _me.Nick, Pv.Orange, Pv.Charcoal);
            Glyphs.StatusDot(g, new RectangleF(box.Right - 10, box.Bottom - 10, 12, 12), Pv.Green, Pv.SurfaceLow);

            using (var b = new SolidBrush(Pv.Bone))
            using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(_me.Nick, Pv.BodyBold, b,
                    new RectangleF(box.Right + 7, box.Y, 79, 18), sf);
            string sub = _me.Badge.Length > 0 ? _me.Badge : _me.PcShort + " PC";
            using (var b = new SolidBrush(_me.Badge.Length > 0 ? Pv.NitroPink : Pv.BoneDim))
            using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(sub, Pv.Label, b, new RectangleF(box.Right + 7, box.Y + 19, 79, 16), sf);
        };

        var mic = new GlyphButton((g, r, c, w) => Glyphs.Mic(g, r, c, _session?.Muted == true))
        { Size = new Size(32, 32) };
        mic.ToolTipText = "Mutar microfone";
        mic.Click += (_, _) =>
        {
            if (_session == null) { OpenSettings(); return; }
            ToggleMute();
            mic.Accent = _session.Muted ? Pv.Red : Pv.Bone;
            mic.ToolTipText = _session.Muted ? "Desmutar microfone" : "Mutar microfone";
            mic.Invalidate();
            UpdateVoiceStrip();
        };

        var headphones = new GlyphButton((g, r, c, w) => Glyphs.Headphones(g, r, c, _deafened))
        { Size = new Size(32, 32) };
        headphones.ToolTipText = "Ensurdecer";
        headphones.Click += (_, _) =>
        {
            ToggleDeafen();
            headphones.Accent = _deafened ? Pv.Red : Pv.Bone;
            headphones.ToolTipText = _deafened ? "Desensurdecer" : "Ensurdecer";
            headphones.Invalidate();
            UpdateVoiceStrip();
        };

        var gear = new GlyphButton(Glyphs.Gear) { Size = new Size(32, 32) };
        gear.ToolTipText = "Configuracoes do usuario";
        gear.Click += (_, _) => OpenSettings();

        void Layout()
        {
            gear.Location = new Point(p.ClientSize.Width - 36, (p.Height - gear.Height) / 2);
            headphones.Location = new Point(p.ClientSize.Width - 70, (p.Height - headphones.Height) / 2);
            mic.Location = new Point(p.ClientSize.Width - 104, (p.Height - mic.Height) / 2);
        }
        p.Resize += (_, _) => Layout();
        p.Controls.AddRange(new Control[] { mic, headphones, gear });

        var menu = new ContextMenuStrip { BackColor = Pv.SurfaceLow, ForeColor = Pv.Bone };
        menu.Items.Add("Configuracoes", null, (_, _) => OpenSettings());
        menu.Items.Add("Trocar de conta", null, (_, _) => Logout());
        p.ContextMenuStrip = menu;
        Layout();
        return p;
    }

    /// <summary>Faixa "voce esta numa call" no rodape do rail (some fora de call).</summary>
    private Panel BuildVoiceStrip()
    {
        var p = new Panel { Dock = DockStyle.Bottom, Height = 0, BackColor = Pv.Char2, Visible = false };
        p.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var pen = new Pen(Pv.Border, 1)) g.DrawLine(pen, 0, 0, p.Width, 0);
            using (var b = new SolidBrush(Pv.Green))
                Pv.DrawTracked(g, "VOZ CONECTADA", Pv.Label, b, 14, 10, 1.6f);
            using (var b = new SolidBrush(Pv.Bone))
                g.DrawString(_voiceRoomName, Pv.Body, b, 14, 24);
        };
        return p;
    }

    private void UpdateVoiceStrip()
    {
        _userPanel?.Invalidate(true);
        if (_voiceStrip == null) return;
        bool on = _session != null;
        _voiceStrip.Visible = on;
        _voiceStrip.Height = on ? 92 : 0;
        _voiceStrip.Controls.Clear();
        if (!on) return;

        var mute = new GlyphButton((g, r, c, w) => Glyphs.Mic(g, r, c, _session!.Muted))
        { Size = new Size(34, 34), Location = new Point(14, 48) };
        mute.Accent = _session!.Muted ? Pv.Red : Pv.Bone;
        mute.ToolTipText = _session.Muted ? "Desmutar" : "Mutar";
        mute.Click += (_, _) => { ToggleMute(); UpdateVoiceStrip(); };

        var deafen = new GlyphButton((g, r, c, w) => Glyphs.Headphones(g, r, c, _deafened))
        { Size = new Size(34, 34), Location = new Point(54, 48) };
        deafen.Accent = _deafened ? Pv.Red : Pv.Bone;
        deafen.ToolTipText = _deafened ? "Desensurdecer" : "Ensurdecer";
        deafen.Click += (_, _) => { ToggleDeafen(); UpdateVoiceStrip(); };

        var leave = new GlyphButton(Glyphs.Exit) { Size = new Size(34, 34), Location = new Point(94, 48) };
        leave.Accent = Pv.Red;
        leave.ToolTipText = "Sair da call";
        leave.Click += (_, _) => LeaveVoice();

        _voiceStrip.Controls.AddRange(new Control[] { mute, deafen, leave });
        _voiceStrip.Invalidate();
    }

    private void OpenSettings()
    {
        using var dlg = new SettingsDialog(_cfg);
        dlg.Applied += () =>
        {
            ApplyClipHotkey();     // a tecla pode ter mudado
            if (_voice != null) _voice.MusicVolume = _cfg.MusicVolume / 100f;
            if (_screenSender != null)
            {
                _screenSender.TotalUploadBudget = Math.Clamp(_cfg.ScreenBudgetKb, 200, 6000) * 1000;
                _screenSender.TargetFps = _cfg.ScreenFps;
                _screenSender.MaxWidth = _cfg.ScreenMaxWidth;
            }
            if (_me != null)
            {
                Pv.SetAccent(_cfg.UseSiteTheme ? _me.ThemeAccent : null);
                Invalidate(true);
            }
            SyncRoomButtons();

            // Ja numa call? Reabre o audio com os dispositivos novos, sem derrubar a sala.
            if (_voice == null || _session == null) return;
            try
            {
                _voice.Dispose();
                _voice = new VoiceEngine { Deafened = _deafened };
                _voice.Failed += ShowBanner;
                _voice.AttachSession(_session);
                _voice.Start(_cfg.MicDevice,
                    string.IsNullOrEmpty(_cfg.OutputDeviceId) ? null : _cfg.OutputDeviceId);
                Log.Write("audio reaberto com os dispositivos novos");
            }
            catch (Exception ex)
            {
                Log.Write("troca de dispositivo falhou: " + ex.Message);
                ShowBanner("Nao consegui trocar o dispositivo: " + ex.Message);
            }
        };
        dlg.ShowDialog(this);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // RAIL
    // ═══════════════════════════════════════════════════════════════════════

    private void RebuildRail()
    {
        if (_railList == null || _railList.IsDisposed) return;
        _railList.SuspendLayout();
        foreach (Control c in _railList.Controls.Cast<Control>().ToList()) c.Dispose();
        _railList.Controls.Clear();

        // Dock=Top empilha ao contrario: montamos a lista e adicionamos invertida.
        var items = new List<Control>();

        var camp = new RailItem("Acampamento", RailItem.Kind.Action)
        { Dock = DockStyle.Top, Active = _view == "home" };
        camp.Click += (_, _) => SelectView("home");
        items.Add(camp);

        items.Add(RailHeader("CANAIS DE TEXTO"));
        foreach (var channel in TextChannels)
        {
            string view = channel.Id == ChatService.GeneralChannel ? "geral" : "channel:" + channel.Id;
            var item = new RailItem(channel.Name, RailItem.Kind.TextChannel)
            { Dock = DockStyle.Top, Active = _view == view };
            item.Click += (_, _) => SelectView(view);
            items.Add(item);
        }

        items.Add(RailHeader("ESCUTA CONJUNTA"));
        int djCount = (_session?.DjJoined == true ? 1 : 0)
                    + (_session?.Peers.Count(p => p.DjJoined) ?? 0);
        var dj = new RailItem("Sala DJ", RailItem.Kind.Music)
        {
            Dock = DockStyle.Top, Active = _view == "dj",
            Suffix = djCount > 0 ? djCount.ToString() : "",
        };
        dj.Click += (_, _) => SelectView("dj");
        items.Add(dj);

        if (_session?.DjJoined == true)
        {
            var me = new RailItem(Nick, RailItem.Kind.Dm)
            {
                Dock = DockStyle.Top, Height = 30, AvatarNick = Nick,
                Online = true, Indent = 14, Suffix = _session.DjHost ? "DJ" : "",
            };
            items.Add(me);
        }
        if (_session != null)
        {
            foreach (var listener in _session.Peers.Where(p => p.DjJoined)
                         .OrderByDescending(p => p.DjHost)
                         .ThenBy(p => p.Nick, StringComparer.OrdinalIgnoreCase))
            {
                var person = new RailItem(listener.Nick, RailItem.Kind.Dm)
                {
                    Dock = DockStyle.Top, Height = 30, AvatarNick = listener.Nick,
                    Online = true, Indent = 14, Suffix = listener.DjHost ? "DJ" : "",
                };
                string nick = listener.Nick;
                person.Click += (_, _) => OpenDm(nick);
                items.Add(person);
            }
        }

        items.Add(RailHeader("SALAS DE VOZ"));
        foreach (var room in _rooms)
        {
            var it = new RailItem(room.Name, RailItem.Kind.Voice)
            {
                Dock = DockStyle.Top,
                Active = _view == "room:" + room.Id,
                Suffix = room.Count > 0 ? room.Count.ToString() : "",
            };
            string id = room.Id, name = room.Name;
            it.Click += async (_, _) => await OnRoomClickedAsync(id, name);
            items.Add(it);

            foreach (string occupant in room.Occupants.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                var person = new RailItem(occupant, RailItem.Kind.Dm)
                {
                    Dock = DockStyle.Top, Height = 30, AvatarNick = occupant,
                    Online = true, Indent = 14,
                };
                string nick = occupant;
                person.Click += (_, _) => OpenDm(nick);
                items.Add(person);
            }
        }
        var novaSala = new RailItem("Nova sala", RailItem.Kind.Action) { Dock = DockStyle.Top };
        novaSala.Click += async (_, _) => await CreateRoomAsync();
        items.Add(novaSala);

        items.Add(RailHeader("ATIVIDADES"));
        var servers = new RailItem("Servidores abertos", RailItem.Kind.Action)
        { Dock = DockStyle.Top, Active = _view == "servers" };
        servers.Click += (_, _) => SelectView("servers");
        items.Add(servers);

        if (_openDms.Count > 0)
        {
            items.Add(RailHeader("CONVERSAS"));
            foreach (string other in _openDms)
            {
                var it = new RailItem(other, RailItem.Kind.Dm)
                {
                    Dock = DockStyle.Top,
                    Active = _view == "dm:" + other,
                    AvatarNick = other,
                    Online = _presence.TryGetValue(other, out var pr) && pr.Online,
                };
                string o = other;
                it.Click += (_, _) => SelectView("dm:" + o);
                items.Add(it);
            }
        }

        items.Reverse();
        foreach (var c in items) _railList.Controls.Add(c);
        _railList.ResumeLayout();
    }

    private static Panel RailHeader(string text)
    {
        var p = new Panel { Dock = DockStyle.Top, Height = 28, BackColor = Pv.Char2 };
        p.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using var b = new SolidBrush(Pv.BoneDim);
            Pv.DrawTracked(g, text, Pv.Label, b, 14, 12, 2.0f);
        };
        return p;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // NAVEGACAO
    // ═══════════════════════════════════════════════════════════════════════

    private void SelectView(string view)
    {
        _view = view;
        if (_contentHost == null) return;

        _contentHost.SuspendLayout();
        _contentHost.Controls.Clear();

        if (view == "home")
        {
            _contentHost.Controls.Add(EnsureDashboard());
            SyncDashboard();
        }
        else if (view == "servers")
        {
            _contentHost.Controls.Add(EnsureServerPanel());
            _ = RefreshGameServersAsync();
        }
        else if (view == "dj")
        {
            _contentHost.Controls.Add(EnsureDjView());
            SyncDjView();
        }
        else if (view.StartsWith("room:"))
        {
            _contentHost.Controls.Add(EnsureRoomPanel());
            if (_preview) ConfigurePreviewRoom();
        }
        else
        {
            if (_chatView == null) return;
            _contentHost.Controls.Add(_chatView);
            if (view == "geral" || view.StartsWith("channel:"))
            {
                string id = view == "geral" ? ChatService.GeneralChannel : view[8..];
                var channel = TextChannels.FirstOrDefault(c => c.Id == id);
                _chatView.SetHeader("# " + (channel.Name ?? id), channel.Description ?? "Canal de texto");
            }
            else
            {
                string other = view[3..];
                _chatView.SetHeader("@ " + other,
                    "Conversa direta — nao e criptografada, evita segredo aqui.");
            }
            _chatView.SetMessages(new List<ChatMessage>(), Nick);
            _chatView.FocusComposer();
            _ = RefreshChatAsync();
        }
        _contentHost.ResumeLayout();
        RebuildRail();
    }

    private DashboardView EnsureDashboard()
    {
        if (_dashboard != null && !_dashboard.IsDisposed) return _dashboard;
        _dashboard = new DashboardView { Dock = DockStyle.Fill };
        _dashboard.RoomRequested += async (id, name) =>
        {
            await OnRoomClickedAsync(id, name);
            SelectView("room:" + id);
        };
        _dashboard.DjRequested += () => SelectView("dj");
        _dashboard.ServersRequested += () => SelectView("servers");
        _dashboard.ClipsRequested += () => SelectView("channel:clipes");
        _dashboard.ShareRequested += () =>
        {
            if (_session == null) ShowBanner("Entre numa sala de voz antes de transmitir.");
            else { SelectView("room:" + _session.RoomId); ToggleScreenShare(); }
        };
        return _dashboard;
    }

    private void SyncDashboard()
    {
        if (_dashboard == null || _dashboard.IsDisposed) return;
        int online = _presence.Count(p => p.Value.Online);
        _dashboard.Configure(_rooms, _preview ? "prévia isolada" : ConnectionPolicy.Summary,
            online, _preview);
    }

    private GameServersPanel EnsureServerPanel()
    {
        if (_serverPanel != null && !_serverPanel.IsDisposed) return _serverPanel;
        _serverPanel = new GameServersPanel { Dock = DockStyle.Fill };
        _serverPanel.AddRequested += AddGameServer;
        _serverPanel.RefreshRequested += () => _ = RefreshGameServersAsync();
        _serverPanel.RemoveRequested += RemoveGameServer;
        return _serverPanel;
    }

    private async Task RefreshGameServersAsync()
    {
        if (_serverPanel == null || _serverPanel.IsDisposed) return;
        try
        {
            var entries = _gameServers.Load();
            var unknown = entries.Select(e => new GameServerStatus(e, GameServerState.Unknown,
                DateTimeOffset.Now)).ToList();
            _serverPanel.SetStatuses(unknown, checking: !_preview && entries.Count > 0);
            if (_preview || entries.Count == 0) return;
            var statuses = await _gameProbe.CheckAllAsync(entries);
            _serverStatuses = statuses.ToDictionary(s => s.Server.Id, StringComparer.OrdinalIgnoreCase);
            _serverPanel.SetStatuses(statuses);
        }
        catch (Exception ex)
        {
            Log.Write("servidores: " + ex.Message);
            ShowBanner("Não consegui ler o cadastro local de servidores: " + ex.Message);
        }
    }

    private void AddGameServer()
    {
        string? name = PromptDialog.Ask(this, "NOVO SERVIDOR", "Nome para a tribo", "ex: Minecraft survival");
        if (string.IsNullOrWhiteSpace(name)) return;
        string? game = PromptDialog.Ask(this, "NOVO SERVIDOR", "Jogo", "Minecraft Java, LoL...");
        if (string.IsNullOrWhiteSpace(game)) return;
        string? host = PromptDialog.Ask(this, "NOVO SERVIDOR", "Host Tailscale ou DNS", "100.64.x.x ou play.exemplo");
        if (string.IsNullOrWhiteSpace(host)) return;
        string? portText = PromptDialog.Ask(this, "NOVO SERVIDOR", "Porta", "25565");
        if (!int.TryParse(portText, out int port)) { ShowBanner("Porta inválida."); return; }
        try
        {
            var entry = new GameServerEntry
            {
                Name = name.Trim(), Game = game.Trim(), Host = host.Trim(), Port = port,
                Kind = game.Contains("minecraft", StringComparison.OrdinalIgnoreCase)
                    ? GameServerKind.MinecraftJava : GameServerKind.Tcp,
            }.Validated();
            var all = _gameServers.Load().ToList();
            all.RemoveAll(x => x.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase));
            all.Add(entry); _gameServers.Save(all);
            _ = RefreshGameServersAsync();
        }
        catch (Exception ex) { ShowBanner("Servidor inválido: " + ex.Message); }
    }

    private void RemoveGameServer(string id)
    {
        try
        {
            var all = _gameServers.Load().Where(x => !x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            _gameServers.Save(all);
            _ = RefreshGameServersAsync();
        }
        catch (Exception ex) { ShowBanner("Não consegui remover o servidor: " + ex.Message); }
    }

    private DjView EnsureDjView()
    {
        if (_djView != null && !_djView.IsDisposed) return _djView;
        _djView = new DjView { Dock = DockStyle.Fill };
        _djView.JoinToggled += ToggleDjMembership;
        _djView.HostToggled += ToggleDjHost;
        _djView.BackToVoice += () =>
        {
            if (_session != null) SelectView("room:" + _session.RoomId);
        };
        return _djView;
    }

    private void SyncDjView()
    {
        if (_djView == null || _djView.IsDisposed) return;
        var session = _session;
        if (session == null)
        {
            _djView.Configure(false, false, false, "", "", "",
                Array.Empty<DjParticipant>());
            return;
        }

        var people = new List<DjParticipant>();
        if (session.DjJoined)
            people.Add(new DjParticipant(Nick, session.DjHost, true));
        people.AddRange(session.Peers.Where(p => p.DjJoined)
            .Select(p => new DjParticipant(p.Nick, p.DjHost, false)));
        people = people.OrderByDescending(p => p.IsHost)
                       .ThenBy(p => p.Nick, StringComparer.OrdinalIgnoreCase).ToList();

        var remoteHost = session.Peers.FirstOrDefault(p => p.DjHost);
        string hostNick = session.DjHost ? Nick : remoteHost?.Nick ?? "";
        string track = session.DjHost ? _nowPlaying : remoteHost?.DjTrack ?? "";
        _djView.Configure(true, session.DjJoined, session.DjHost,
            _voiceRoomName, track, hostNick, people);
    }

    private async Task OnRoomClickedAsync(string roomId, string roomName)
    {
        if (_preview)
        {
            _voiceRoomId = roomId; _voiceRoomName = roomName;
            SelectView("room:" + roomId);
            return;
        }
        // Ja estou nessa call? So mostra os tiles. Senao, entra.
        if (_voiceRoomId != roomId) await JoinVoiceAsync(roomId, roomName);
        SelectView("room:" + roomId);
    }

    private void OpenDm(string other)
    {
        other = other.ToLowerInvariant();
        if (string.Equals(other, Nick, StringComparison.OrdinalIgnoreCase)) return;
        if (!_openDms.Contains(other)) _openDms.Add(other);
        SelectView("dm:" + other);
    }

    private async Task OnSendMessageAsync(string text)
    {
        if (_chat == null) return;
        try
        {
            if (_view == "geral") await _chat.SendToChannelAsync(ChatService.GeneralChannel, text);
            else if (_view.StartsWith("channel:")) await _chat.SendToChannelAsync(_view[8..], text);
            else if (_view.StartsWith("dm:")) await _chat.SendDmAsync(_view[3..], text);
            await RefreshChatAsync();
        }
        catch (FirestoreException ex) when (ex.IsPermissionDenied)
        {
            ShowBanner("O Firestore recusou — falta publicar as rules (pc_chat/pc_dm) no Console.");
        }
        catch (Exception ex)
        {
            Log.Write("envio de mensagem falhou: " + ex.Message);
            ShowBanner("Nao consegui enviar: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // POLL (Firestore nao tem listener no REST)
    // ═══════════════════════════════════════════════════════════════════════

    private async Task PollAsync()
    {
        if (_polling || _me == null || _chat == null) return;
        _polling = true;
        try
        {
            _pollTick++;

            // Heartbeat de presenca a cada ~20s.
            if (_pollTick % 10 == 1)
                try { await _chat.HeartbeatAsync(_voiceRoomId.Length > 0 ? _voiceRoomName : ""); }
                catch (FirestoreException ex) when (ex.IsPermissionDenied)
                {
                    ShowBanner("O Firestore recusou a escrita — falta publicar as rules no Console.");
                    _pollTimer?.Stop();
                    return;
                }

            if (_members.Count == 0) _members = await Primitivao.ListMembersAsync(_fs);

            try { _presence = await _chat.ReadPresenceAsync(); } catch { }

            var rooms = await _dir.ListAsync();
            bool roomsChanged = rooms.Count != _rooms.Count ||
                rooms.Zip(_rooms).Any(t => t.First.Id != t.Second.Id || t.First.Count != t.Second.Count);
            _rooms = rooms;
            SyncDashboard();

            await RefreshChatAsync();
            RefreshMembers();
            if (roomsChanged || _pollTick % 5 == 1) RebuildRail();
        }
        catch (FirestoreException ex) when (ex.IsPermissionDenied)
        {
            ShowBanner("O Firestore recusou a leitura — falta publicar as rules no Firebase Console.");
            _pollTimer?.Stop();
        }
        catch (Exception ex) { Log.Write("poll falhou: " + ex.Message); }
        finally { _polling = false; }
    }

    private async Task RefreshChatAsync()
    {
        if (_chat == null || _chatView == null || _chatView.IsDisposed) return;
        if (_view.StartsWith("room:") || _view == "dj") return;
        try
        {
            var msgs = _view == "geral"
                ? await _chat.ReadChannelAsync(ChatService.GeneralChannel)
                : _view.StartsWith("channel:")
                    ? await _chat.ReadChannelAsync(_view[8..])
                    : await _chat.ReadDmAsync(_view[3..]);
            if (!_chatView.IsDisposed) _chatView.SetMessages(msgs, Nick);
        }
        catch (FirestoreException ex) when (ex.IsPermissionDenied) { throw; }
        catch (Exception ex) { Log.Write("ler chat falhou: " + ex.Message); }
    }

    private void RefreshMembers()
    {
        if (_membersList == null || _membersList.IsDisposed) return;

        var ordered = _members
            .OrderByDescending(n => _presence.TryGetValue(n, out var p) && p.Online)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Recria so quando a composicao muda (o poll roda a cada 2s).
        string sig = string.Join(",", ordered.Select(n =>
            n + (_presence.TryGetValue(n, out var p) && p.Online ? "+" + p.Room : "-")));
        if ((string?)_membersList.Tag == sig) return;
        _membersList.Tag = sig;

        _membersList.SuspendLayout();
        foreach (Control c in _membersList.Controls.Cast<Control>().ToList()) c.Dispose();
        _membersList.Controls.Clear();

        var rows = new List<Control>();
        int online = ordered.Count(n => _presence.TryGetValue(n, out var p) && p.Online);
        rows.Add(RailHeader($"ONLINE — {online}"));
        bool addedOffline = false;
        foreach (string n in ordered)
        {
            bool isOn = _presence.TryGetValue(n, out var pr) && pr.Online;
            if (!isOn && !addedOffline)
            {
                rows.Add(RailHeader($"OFFLINE — {ordered.Count - online}"));
                addedOffline = true;
            }
            var row = new MemberRow
            {
                Dock = DockStyle.Top, Nick = n, Online = isOn,
                Room = isOn ? (pr!.Room ?? "") : "",
                IsMe = string.Equals(n, Nick, StringComparison.OrdinalIgnoreCase),
            };
            string target = n;
            row.Click += (_, _) => OpenDm(target);
            rows.Add(row);
        }
        rows.Reverse();
        foreach (var c in rows) _membersList.Controls.Add(c);
        _membersList.ResumeLayout();
    }

    private async Task CreateRoomAsync()
    {
        string? name = PromptDialog.Ask(this, "NOVA SALA DE VOZ", "Nome da sala",
                                        "ex: RANQUEADA, RESENHA...");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var room = await _dir.CreateAsync(name.Trim(), Nick);
            _rooms = await _dir.ListAsync();
            RebuildRail();
            await JoinVoiceAsync(room.Id, room.Name);
            SelectView("room:" + room.Id);
        }
        catch (FirestoreException ex) when (ex.IsPermissionDenied)
        {
            ShowBanner("O Firestore recusou a escrita — falta publicar as rules do pc_rooms no Console.");
        }
        catch (Exception ex)
        {
            Log.Write("criar sala falhou: " + ex.Message);
            ShowBanner("Nao consegui criar a sala: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // VOZ
    // ═══════════════════════════════════════════════════════════════════════

    private Panel EnsureRoomPanel()
    {
        if (_roomPanel != null && !_roomPanel.IsDisposed) return _roomPanel;

        _roomPanel = new Panel { Dock = DockStyle.Fill, BackColor = Pv.Charcoal };

        var head = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Pv.Charcoal };
        head.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var p = new Pen(Pv.Char3, 2)) g.DrawLine(p, 0, head.Height - 1, head.Width, head.Height - 1);
            var ic = new RectangleF(20, 18, 18, 18);
            Glyphs.Speaker(g, ic, Pv.Bone);
            using var b = new SolidBrush(Pv.Bone);
            g.DrawString(_voiceRoomName, Pv.DisplaySm, b, 46, 12);
        };

        _roomStatus = new Label
        {
            Dock = DockStyle.Top, Height = 26, Font = Pv.Body, ForeColor = Pv.BoneDim,
            Padding = new Padding(22, 4, 0, 0), Text = "conectando...",
        };
        // "tocando agora" vive na linha de status, que tem a largura toda — na barra
        // de botoes ele era cortado pelo painel de membros.
        _djLabel = new Label
        {
            Dock = DockStyle.Top, Height = 22, Font = Pv.Label, ForeColor = Pv.Green,
            Padding = new Padding(22, 2, 0, 0), Text = "", Visible = false,
        };

        // Barra de acoes da sala: tela, clipe, gravar, DJ.
        var actions = BuildRoomActions();

        // Tiles embaixo; palco ocupa o resto. Altura = tile (124) + margem (12) +
        // padding (16) + folga da barra de rolagem horizontal.
        _tiles = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 168, AutoScroll = true, WrapContents = false,
            BackColor = Pv.Charcoal, Padding = new Padding(16, 8, 16, 8),
        };

        _stage = new StageView { Dock = DockStyle.Fill };

        // A barra de icones fica encostada nos participantes (Dock=Top dentro de um
        // container que tambem segura os tiles), pra ficar claro que agem sobre a call.
        var rodape = new Panel { Dock = DockStyle.Bottom, BackColor = Pv.Charcoal, AutoSize = true };
        rodape.Paint += (_, e) =>
        {
            using var p = new Pen(Pv.Char3, 2);
            e.Graphics.DrawLine(p, 16, 0, rodape.Width - 16, 0);
        };
        rodape.Controls.Add(_tiles);
        rodape.Controls.Add(actions);

        _roomPanel.Controls.Add(_stage);      // Fill primeiro
        _roomPanel.Controls.Add(rodape);
        _roomPanel.Controls.Add(_djLabel);
        _roomPanel.Controls.Add(_roomStatus);
        _roomPanel.Controls.Add(head);
        return _roomPanel;
    }

    private void ConfigurePreviewRoom()
    {
        if (_tiles == null || _tiles.IsDisposed) return;
        _tiles.SuspendLayout();
        foreach (Control old in _tiles.Controls.Cast<Control>().ToList()) old.Dispose();
        _tiles.Controls.Clear();
        var room = _rooms.FirstOrDefault(r => r.Id == _voiceRoomId);
        foreach (string nick in room?.Occupants ?? new List<string> { "bane", "mohamed", "ricle", "vitinho", "angu" })
        {
            var tile = new PeerTile
            {
                Nick = nick, IsMe = nick.Equals(Nick, StringComparison.OrdinalIgnoreCase),
                Connected = true, Sharing = nick is "vitinho", DjJoined = nick is "bane" or "mohamed",
                DjHost = nick == "bane", Margin = new Padding(6),
            };
            _tiles.Controls.Add(tile);
        }
        _tiles.ResumeLayout();
        if (_roomStatus != null) _roomStatus.Text = "5 na call · Tailscale P2P · modo de prévia visual";
        if (_djLabel != null) { _djLabel.Text = "SALA DJ · Bane e Mohamed estão ouvindo juntos"; _djLabel.Visible = true; }
        SyncRoomButtons();
    }

    private ActionIcon? _icMic, _icShare, _icRec, _icClip, _icDj, _icCinema, _icQuick, _icLeave;
    private Label? _lanBadge;
    private CinemaSession? _cinema;
    private Form? _cinemaWindow;

    /// <summary>
    /// Barra de icones acima dos participantes. Antes eram botoes de texto largos:
    /// cinco rotulos por extenso somavam ~800px e o ultimo saia da tela.
    /// </summary>
    private Panel BuildRoomActions()
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            BackColor = Pv.Charcoal,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(18, 8, 18, 4),
        };

        _icMic = new ActionIcon((g, r, c, w) => Glyphs.Mic(g, r, c, _session?.Muted == true), "MIC")
        { ToolTipText = "Mutar / desmutar o microfone" };
        _icMic.Click += (_, _) => { ToggleMute(); SyncRoomButtons(); };

        _icShare = new ActionIcon((g, r, c, w) => Glyphs.Screen(g, r, c, _iAmSharing), "TELA")
        { ToolTipText = "Compartilhar tela ou janela (com som)" };
        _icShare.Click += (_, _) => ToggleScreenShare();

        _icRec = new ActionIcon(Glyphs.Record, "BUFFER")
        { ToolTipText = "Liga o buffer que permite salvar o que ja passou" };
        _icRec.Click += (_, _) => ToggleClipBuffer();

        _icClip = new ActionIcon(Glyphs.Scissors, "CLIPE")
        { ToolTipText = "Salvar os ultimos segundos" };
        _icClip.Click += (_, _) => SaveClip();

        _icDj = new ActionIcon(Glyphs.Music, "DJ")
        { ToolTipText = "Abrir a escuta musical paralela a call" };
        _icDj.Click += (_, _) => SelectView("dj");

        _icCinema = new ActionIcon(Glyphs.Film, "CINEMA")
        { ToolTipText = "Assistir um video junto, na qualidade original" };
        _icCinema.Click += (_, _) => OpenCinema();

        _icQuick = new ActionIcon(Glyphs.Gear, "AJUSTES")
        { ToolTipText = "Microfone, saida e qualidade (sem sair da call)" };
        _icQuick.Click += (_, _) => OpenQuickSettings();

        _icLeave = new ActionIcon(Glyphs.Exit, "SAIR") { ToolTipText = "Sair da sala de voz" };
        _icLeave.Click += (_, _) => LeaveVoice();

        _lanBadge = new Label
        {
            Font = Pv.Label, ForeColor = Pv.Green, AutoSize = true, Text = "",
            Margin = new Padding(14, 22, 0, 0), Visible = false,
        };

        foreach (var ic in new[] { _icMic, _icShare, _icRec, _icClip, _icDj, _icCinema, _icQuick, _icLeave })
            ic!.Margin = new Padding(0, 0, 6, 0);
        bar.Controls.AddRange(new Control[]
            { _icMic, _icShare, _icRec, _icClip, _icDj, _icCinema, _icQuick, _icLeave, _lanBadge });
        return bar;
    }

    private void OpenQuickSettings()
    {
        if (_icQuick == null) return;
        var pos = _icQuick.PointToScreen(new Point(_icQuick.Width / 2, 0));
        using var q = new QuickSettings(_cfg, pos);
        q.Applied += () =>
        {
            if (_screenSender != null)
            {
                _screenSender.TotalUploadBudget = Math.Clamp(_cfg.ScreenBudgetKb, 200, 6000) * 1000;
                _screenSender.TargetFps = _cfg.ScreenFps;
                _screenSender.MaxWidth = _cfg.ScreenMaxWidth;
            }
            _screenAudio = !_djMode && _cfg.ShareAudioWithScreen;
            SyncSystemAudio();
            // Dispositivo pode ter mudado: reabre o audio sem derrubar a sala.
            if (_voice != null && _session != null)
            {
                try
                {
                    _voice.Dispose();
                    _voice = new VoiceEngine
                    {
                        MusicVolume = _cfg.MusicVolume / 100f,
                        Deafened = _deafened,
                    };
                    _voice.Failed += ShowBanner;
                    _voice.AttachSession(_session);
                    _voice.HeardPcm += (b, o, c) => _clips?.PushHeard(b, o, c);
                    _voice.MicPcm += (b, o, c) => _clips?.PushMic(b, o, c);
                    _voice.Start(_cfg.MicDevice,
                        string.IsNullOrEmpty(_cfg.OutputDeviceId) ? null : _cfg.OutputDeviceId);
                }
                catch (Exception ex) { Log.Write("troca rapida de dispositivo: " + ex.Message); }
            }
            SyncRoomButtons();
        };
        q.ShowDialog(this);
    }

    private async Task JoinVoiceAsync(string roomId, string roomName)
    {
        LeaveVoice();
        _voiceRoomId = roomId;
        _voiceRoomName = roomName;

        string peerId = Sanitize(Nick) + "-" + Random.Shared.Next(0x10000, 0xFFFFF).ToString("x5");
        _session = new RoomSession(_fs, roomId, peerId, Nick)
        {
            TailscaleOnly = _cfg.TailscaleOnly,
        };
        _session.PeersChanged += OnPeersChanged;
        _session.Failed += ShowBanner;

        _session.ScreenFrameReceived += OnPeerFrame;

        _voice = new VoiceEngine
        {
            MusicVolume = _cfg.MusicVolume / 100f,
            Deafened = _deafened,
        };
        _voice.Failed += ShowBanner;
        _voice.AttachSession(_session);

        // Buffer rolante de clipe: recebe o que eu ouço e o meu microfone.
        _clips = new ClipRecorder(60);
        _voice.HeardPcm += (b, o, c) => _clips?.PushHeard(b, o, c);
        _voice.MicPcm += (b, o, c) => _clips?.PushMic(b, o, c);

        // Recria os tiles do zero pra esta sala.
        _peerTiles.Clear();
        EnsureRoomPanel();
        _tiles!.Controls.Clear();
        _myTile = new PeerTile { Nick = Nick, IsMe = true, Connected = true, Margin = new Padding(6) };
        _tiles.Controls.Add(_myTile);

        UpdateVoiceStrip();

        try
        {
            _voice.Start(_cfg.MicDevice,
                string.IsNullOrEmpty(_cfg.OutputDeviceId) ? null : _cfg.OutputDeviceId);
            await _session.StartAsync();
            UpdateRoomStatus();
        }
        catch (FirestoreException ex) when (ex.IsPermissionDenied)
        {
            ShowBanner("O Firestore recusou a escrita — falta publicar as rules do pc_rooms no Console.");
            LeaveVoice();
            return;
        }
        catch (Exception ex)
        {
            Log.Write("entrar na sala falhou: " + ex.Message);
            ShowBanner("Nao consegui entrar: " + ex.Message);
            LeaveVoice();
            return;
        }

        SyncRoomButtons();

        _voiceTimer?.Stop();
        _voiceTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _voiceTimer.Tick += (_, _) => TickVoice();
        _voiceTimer.Start();
        _stageTimer?.Dispose();
        _stageTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _stageTimer.Tick += (_, _) => TickStage();
        _stageTimer.Start();
    }

    private void LeaveVoice()
    {
        _voiceTimer?.Stop();
        _voiceTimer?.Dispose();
        _voiceTimer = null;
        try { _stageTimer?.Stop(); _stageTimer?.Dispose(); } catch { }
        _stageTimer = null;

        try { _cinemaWindow?.Close(); } catch { }
        _cinemaWindow = null;
        try { _cinema?.Dispose(); } catch { }
        _cinema = null;
        _bufferOffByUser = false;

        try { _screenSender?.Dispose(); } catch { }
        _screenSender = null;
        _iAmSharing = false;
        try { _music?.Dispose(); } catch { }
        _music = null;
        _djMode = false;
        try { _clips?.Dispose(); } catch { }
        _clips = null;
        _screens.Dispose();
        _focusedSharer = 0;
        _nowPlaying = "";

        try { _voice?.Dispose(); } catch { }
        try { _session?.Dispose(); } catch { }
        _voice = null;
        _session = null;
        _deafened = false;
        _mutedBeforeDeafen = false;
        _voiceRoomId = "";
        _voiceRoomName = "";
        _peerTiles.Clear();
        _myTile = null;

        UpdateVoiceStrip();
        _userPanel?.Invalidate(true);
        if (_view.StartsWith("room:")) SelectView("geral");
    }

    private void OnPeersChanged()
    {
        if (InvokeRequired) { BeginInvoke(OnPeersChanged); return; }
        if (_session == null) return;

        var peers = _session.Peers;
        // Duas pessoas podem clicar em "transmitir" no mesmo intervalo de poll.
        // Todos resolvem o empate pela mesma ordem, mantendo apenas um DJ no ar.
        if (_session.DjHost)
        {
            var contender = peers.Where(p => p.DjHost)
                .OrderBy(p => p.Nick, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.PeerId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (contender != null)
            {
                int byNick = StringComparer.OrdinalIgnoreCase.Compare(contender.Nick, Nick);
                bool remoteWins = byNick < 0 || (byNick == 0 &&
                    StringComparer.Ordinal.Compare(contender.PeerId, _session.PeerId) < 0);
                if (remoteWins)
                {
                    _djMode = false;
                    _nowPlaying = "";
                    _screenAudio = _iAmSharing && _cfg.ShareAudioWithScreen;
                    _audioTarget = _screenAudioTarget;
                    _session.SetDjState(true, false);
                    SyncSystemAudio();
                    Toast("SALA DJ", contender.Nick + " ficou no controle da musica.");
                }
            }
        }
        SyncDjView();
        SyncRoomButtons();
        RebuildRail();
        if (_tiles == null || _tiles.IsDisposed) return;
        var alive = peers.Select(p => p.SenderId).ToHashSet();

        foreach (var p in peers)
        {
            if (!_peerTiles.TryGetValue(p.SenderId, out var tile))
            {
                tile = new PeerTile { Margin = new Padding(6) };
                _peerTiles[p.SenderId] = tile;
                _tiles.Controls.Add(tile);
            }
            tile.Nick = p.Nick;
            tile.Muted = p.Muted;
            tile.Sharing = p.Sharing;
            tile.DjJoined = p.DjJoined;
            tile.DjHost = p.DjHost;
            tile.Connected = p.Connected;
            tile.Punching = p.Locked == null;
            // Clicar no tile de quem compartilha joga a tela dele no palco.
            if (p.Sharing && (string?)tile.Tag != "clickable")
            {
                tile.Tag = "clickable";
                uint sid = p.SenderId;
                tile.Cursor = Cursors.Hand;
                tile.Click += (_, _) => { _focusedSharer = sid; };
            }
            tile.Invalidate();
        }

        // Bipe de entrada/saida — util quando a janela esta minimizada.
        if (_cfg.JoinLeaveSound && _peerTiles.Count > 0)
        {
            if (peers.Count > _peerTiles.Count) Chime.Ok();
            else if (peers.Count < _peerTiles.Count) Chime.Fail();
        }

        foreach (uint sid in _peerTiles.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            var tile = _peerTiles[sid];
            _peerTiles.Remove(sid);
            _tiles.Controls.Remove(tile);
            tile.Dispose();
            _voice?.RemovePeer(sid);
            _screens.Remove(sid);
            if (_focusedSharer == sid) _focusedSharer = 0;
        }
        UpdateRoomStatus();
    }

    private int _stageTick;

    private void TickStage()
    {
        if (_stage == null || _stage.IsDisposed || _session == null) return;
        if (_focusedSharer != 0)
        {
            _stage.SetFrame(_screens.FrameOf(_focusedSharer));
            var who = _session.Peers.FirstOrDefault(p => p.SenderId == _focusedSharer);
            _stage.SharerNick = who?.Nick ?? "";
        }
        else
        {
            _stage.SharerNick = "";
            _stage.SetFrame(null);
        }
        bool self = _iAmSharing && _focusedSharer == 0;
        _stage.SelfPreview = self;
        _stage.SelfInfo = self && _screenSender != null
            ? $"{_screenSender.OutWidth}x{_screenSender.OutHeight} · {_screenSender.Fps} FPS · {_screenSender.KbPerSecond} KB/s"
            : "";
        bool buffering = _clips?.Active == true;
        _stage.Recording = buffering;
        string net = _iAmSharing && _screenSender != null
            ? $"{_screenSender.Fps}FPS · Q{_screenSender.Quality} · {_screenSender.KbPerSecond}KB/s"
            : "";
        _stage.StatusRight = buffering
            ? $"BUFFER {_clips!.BufferSeconds}s" + (net.Length > 0 ? " · " + net : "")
            : net;
        _stage.Invalidate();
    }

    private void TickVoice()
    {
        if (_voice == null || _session == null) return;

        // "Tocando agora" a cada ~2s quando sou o DJ.
        if (_djMode && _music != null && ++_stageTick % 20 == 0)
        {
            _ = NowPlaying.ReadAsync().ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully || t.Result == _nowPlaying) return;
                try
                {
                    BeginInvoke(() =>
                    {
                        if (!_djMode || _session == null) return;
                        _nowPlaying = t.Result;
                        _session.SetDjTrack(_nowPlaying);
                        SyncRoomButtons();
                        SyncDjView();
                    });
                }
                catch { }
            });
        }

        if (_myTile != null && !_myTile.IsDisposed)
        {
            _myTile.Level = _session.Muted ? 0f : _voice.MyPeak;
            _myTile.Muted = _session.Muted;
            _myTile.DjJoined = _session.DjJoined;
            _myTile.DjHost = _session.DjHost;
            _myTile.Invalidate();
        }
        foreach (var p in _session.Peers)
        {
            if (!_peerTiles.TryGetValue(p.SenderId, out var tile) || tile.IsDisposed) continue;
            tile.Level = _voice.PeakOf(p.SenderId);
            bool conn = p.Connected, punch = p.Locked == null;
            if (tile.Connected != conn || tile.Punching != punch)
            {
                tile.Connected = conn;
                tile.Punching = punch;
                UpdateRoomStatus();
            }
            tile.Invalidate();
        }
    }

    private void UpdateRoomStatus()
    {
        if (_roomStatus == null || _roomStatus.IsDisposed || _session == null) return;
        var peers = _session.Peers;
        int connected = peers.Count(p => p.Connected);
        int punching = peers.Count(p => !p.Connected);

        string s = peers.Count == 0 ? "você está sozinho na sala — chama a galera"
                 : punching == 0 ? $"{connected + 1} na call · "
                    + (_session.TailscaleOnly ? "Tailscale P2P" : "conectado direto (P2P)")
                 : $"{connected + 1} na call · {punching} conectando...";
        int djListeners = peers.Count(p => p.DjJoined) + (_session.DjJoined ? 1 : 0);
        if (djListeners > 0) s += $" · {djListeners} na escuta DJ";
        if (!_session.TailscaleOnly && _session.PublicEndpoint == null)
            s += " · sem STUN (somente rede local)";
        if (_session.TailscaleOnly && ConnectionPolicy.TailnetAddresses().Count == 0)
            s += " · Tailscale desconectado";
        _roomStatus.Text = s;
    }

    private void ToggleMute()
    {
        if (_session == null) return;
        if (_deafened)
        {
            _deafened = false;
            if (_voice != null) _voice.Deafened = false;
            _session.Muted = false;
            _mutedBeforeDeafen = false;
            _userPanel?.Invalidate(true);
            return;
        }
        _session.Muted = !_session.Muted;
        _userPanel?.Invalidate(true);
    }

    private void ToggleDeafen()
    {
        if (_voice == null)
        {
            Toast("AUDIO", "Entra numa sala de voz para ensurdecer.");
            return;
        }
        _deafened = !_deafened;
        _voice.Deafened = _deafened;
        if (_session != null)
        {
            if (_deafened)
            {
                _mutedBeforeDeafen = _session.Muted;
                _session.Muted = true;
            }
            else
            {
                _session.Muted = _mutedBeforeDeafen;
                _mutedBeforeDeafen = false;
            }
        }
        _userPanel?.Invalidate(true);
    }

    // ─── TELA ────────────────────────────────────────────────────────────────

    private AudioCaptureTarget? ChooseAudioTarget(CaptureTarget? visualTarget, string title = "ÁUDIO DA TRANSMISSÃO")
    {
        var sources = new List<AudioCaptureTarget>();
        if (visualTarget?.IsWindow == true)
        {
            try { sources.Add(visualTarget.ToAudioTarget()); } catch { }
        }
        else sources.Add(AudioCaptureTarget.System);
        sources.Add(AudioCaptureTarget.Silent);
        // A imagem pode ser um monitor inteiro, mas o áudio pode vir só do
        // Spotify, navegador ou jogo. A lista é um snapshot de janelas abertas;
        // o process loopback acompanha a árvore do processo escolhido.
        try
        {
            foreach (var app in AudioCaptureTarget.ListApplications()
                .Where(a => sources.All(s => s.ProcessId != a.ProcessId)).Take(24)) sources.Add(app);
        }
        catch (Exception ex) { Log.Write("listar fontes de áudio: " + ex.Message); }
        if (sources.All(s => s.Kind != AudioCaptureKind.System)) sources.Add(AudioCaptureTarget.System);
        var names = sources.Select(s => s.Name).ToList();
        int pick = PickDialog.Choose(this, title,
            "A imagem e o áudio são escolhas independentes. Nenhum áudio é capturado sem selecionar.", names);
        return pick >= 0 ? sources[pick] : null;
    }

    private void ToggleScreenShare()
    {
        if (_session == null) { ShowBanner("Entra numa sala de voz primeiro."); return; }

        if (_iAmSharing)
        {
            _screenSender?.Dispose();
            _screenSender = null;
            _iAmSharing = false;
            _session.Sharing = false;
            SyncSystemAudio();      // sem tela, o som do sistema so segue se for DJ
            SyncRoomButtons();
            return;
        }

        // Seletor no estilo do OBS: monitores E janelas abertas.
        var targets = CaptureTarget.List();
        if (targets.Count == 0) { ShowBanner("Nao achei nada pra capturar."); return; }
        int pick = PickDialog.Choose(this, "COMPARTILHAR", "O que voce quer mostrar?",
                                     targets.Select(t => t.Name).ToList());
        if (pick < 0) return;

        AudioCaptureTarget? chosenAudio = _cfg.ShareAudioWithScreen
            ? ChooseAudioTarget(targets[pick], "ÁUDIO DA TELA")
            : AudioCaptureTarget.Silent;
        if (chosenAudio == null) return;

        try
        {
            _screenSender = new ScreenSender(_session, targets[pick])
            {
                TotalUploadBudget = Math.Clamp(_cfg.ScreenBudgetKb, 200, 6000) * 1000,
                TargetFps = _cfg.ScreenFps,
                MaxWidth = _cfg.ScreenMaxWidth,
            };
            _screenSender.FullFrameProduced += OnMyFrame;
            _screenSender.TargetLost += () =>
            {
                try { BeginInvoke(() => { if (_iAmSharing) ToggleScreenShare(); }); } catch { }
            };
            // Quadro inteiro custa um encode a mais — so quando o clipe precisa.
            _screenSender.NeedFullFrames = _cfg.AutoBuffer;
            _screenSender.Start();
            _iAmSharing = true;
            _session.Sharing = true;
            _focusedSharer = 0;   // foca a minha propria tela
            _screenAudioTarget = chosenAudio;
            _audioTarget = chosenAudio;
            // Tela sem som e tela pela metade: o pessoal veria o jogo mudo.
            _screenAudio = chosenAudio.Kind != AudioCaptureKind.Silent;
            if (_djMode && _cfg.ShareAudioWithScreen)
                Toast("SALA DJ", "O audio da tela ficou restrito a quem entrou na escuta DJ.");
            SyncSystemAudio();
            SyncRoomButtons();
        }
        catch (Exception ex)
        {
            Log.Write("compartilhar tela falhou: " + ex.Message);
            ShowBanner("Nao consegui capturar a tela: " + ex.Message);
        }
    }

    /// <summary>
    /// Meu proprio quadro — serve SO pro buffer de clipe. Nao decodificamos pra
    /// exibir: o palco mostra um cartao quando sou eu que transmito (ver a propria
    /// tela criava espelho infinito), entao decodificar aqui seria trabalho jogado
    /// fora 30 vezes por segundo.
    /// </summary>
    private void OnMyFrame(byte[] jpeg, int w, int h)
    {
        AutoStartBuffer();
        _clips?.PushFrame(jpeg, w, h);
    }

    private void OnPeerFrame(uint senderId, byte[] payload, int w, int h)
    {
        if (!_screens.OnUpdate(senderId, payload, w, h)) return;
        // Ninguem em foco ainda? A primeira tela que aparecer vira o palco.
        if (_focusedSharer == 0 && !_iAmSharing) _focusedSharer = senderId;
        if (_focusedSharer != senderId) return;

        AutoStartBuffer();
        // Chegam blocos, nao um quadro inteiro — pro clipe, codificamos a tela
        // remontada. So quando o buffer esta gravando, pra nao gastar CPU a toa.
        if (_clips?.Active != true || Environment.TickCount64 < _nextPeerClipAt) return;
        _nextPeerClipAt = Environment.TickCount64 + 1000L / Math.Max(1, _clips.CaptureFps);
        var jpeg = _screens.EncodeFrame(senderId);
        if (jpeg == null) return;
        var (cw, ch) = _screens.SizeOf(senderId);
        _clips.PushFrame(jpeg, cw, ch);
    }

    /// <summary>
    /// Liga o buffer sozinho quando ha tela pra gravar. Sem isso o atalho falharia
    /// calado — ninguem lembra de apertar GRAVAR ANTES da jogada acontecer.
    /// Desligavel na engrenagem; o selo BUFFER no palco deixa visivel que esta ligado.
    /// </summary>
    private void AutoStartBuffer()
    {
        if (_bufferOffByUser) return;   // respeita quem desligou na mao
        if (!_cfg.AutoBuffer || _clips == null || _clips.Active) return;
        _clips.Start();
        if (_screenSender != null) _screenSender.NeedFullFrames = true;
        try { BeginInvoke(SyncRoomButtons); } catch { }
    }

    // ─── CLIPE ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Liga/desliga o buffer na mao. Desligar aqui vale pra sessao inteira: o
    /// auto-ligar nao briga com a decisao do usuario ate ele entrar noutra sala.
    /// </summary>
    private void ToggleClipBuffer()
    {
        if (_clips == null) return;
        if (_clips.Active)
        {
            _clips.Stop(); _bufferOffByUser = true;
            if (_screenSender != null) _screenSender.NeedFullFrames = false;
        }
        else
        {
            _clips.Start(); _bufferOffByUser = false;
            if (_screenSender != null) _screenSender.NeedFullFrames = true;
        }
        SyncRoomButtons();
    }

    private bool _bufferOffByUser;

    private async void SaveClip()
    {
        if (_clips == null || !_clips.Active)
        {
            ShowBanner("Liga o GRAVAR primeiro — o clipe sai do que ficou no buffer.");
            return;
        }
        var result = await _clips.SaveClipAsync(_cfg.ClipSeconds, _voiceRoomName);
        if (!result.Success)
        {
            ShowBanner(result.Status switch
            {
                ClipSaveStatus.Busy => "Já estou exportando um clipe — aguarda terminar.",
                ClipSaveStatus.NoFrames => "Buffer ainda vazio — espera uns segundos.",
                ClipSaveStatus.Cancelled => "Exportação cancelada.",
                _ => "Não consegui exportar o clipe: " + (result.Error ?? result.Status.ToString()),
            });
            return;
        }
        string path = result.Path!;

        // Abre a pasta com o arquivo ja selecionado.
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception ex) { Log.Write("abrir pasta falhou: " + ex.Message); }
    }

    // ─── MODO DJ ─────────────────────────────────────────────────────────────

    private bool _djMode;
    private bool _screenAudio;
    private AudioCaptureTarget _audioTarget = AudioCaptureTarget.System;
    private AudioCaptureTarget _screenAudioTarget = AudioCaptureTarget.System;

    private void ToggleDjMembership()
    {
        if (_session == null) { ShowBanner("Entra numa sala de voz primeiro."); return; }

        if (_session.DjJoined)
        {
            _djMode = false;
            _nowPlaying = "";
            _screenAudio = _iAmSharing && _cfg.ShareAudioWithScreen;
            _audioTarget = _screenAudioTarget;
            _session.SetDjState(false, false);
            _voice?.ClearMusicStreams();
            Toast("SALA DJ", "Voce saiu da escuta. A call continua normal.");
        }
        else
        {
            _session.SetDjState(true, false);
            Toast("SALA DJ", "Voce entrou na escuta sem sair da call.");
        }
        SyncSystemAudio();
        SyncRoomButtons();
        SyncDjView();
        RebuildRail();
        UpdateRoomStatus();
    }

    private void ToggleDjHost()
    {
        if (_session == null) { ShowBanner("Entra numa sala de voz primeiro."); return; }
        if (!_session.DjJoined) { ShowBanner("Entra na escuta DJ primeiro."); return; }

        if (_session.DjHost)
        {
            _djMode = false;
            _nowPlaying = "";
            _screenAudio = _iAmSharing && _cfg.ShareAudioWithScreen;
            _audioTarget = _screenAudioTarget;
            _session.SetDjState(true, false);
            Toast("SALA DJ", "Transmissao encerrada. Voce continua ouvindo a call.");
        }
        else
        {
            var current = _session.Peers.FirstOrDefault(p => p.DjHost);
            if (current != null)
            {
                ShowBanner(current.Nick + " ja esta transmitindo musica nesta sala.");
                return;
            }
            var djSource = ChooseAudioTarget(null, "FONTE DA ESCUTA DJ");
            if (djSource == null || djSource.Kind == AudioCaptureKind.Silent)
            {
                ShowBanner("Escolha um aplicativo ou o áudio do PC para ser o DJ.");
                return;
            }
            _audioTarget = djSource;
            _djMode = true;
            if (_iAmSharing && _screenAudio)
            {
                _screenAudio = false;
                Toast("SALA DJ", "O audio do sistema agora vai somente para a escuta DJ.");
            }
            _session.SetDjState(true, true, _nowPlaying);
        }

        SyncSystemAudio();
        SyncRoomButtons();
        SyncDjView();
        RebuildRail();
        UpdateRoomStatus();
    }

    /// <summary>
    /// Liga/desliga a captura do som do sistema. Existe UMA so, compartilhada entre
    /// "tela com audio" e "modo DJ" — abrir duas capturas do mesmo dispositivo
    /// duplicaria o audio na sala.
    /// </summary>
    private void SyncSystemAudio()
    {
        bool screenForEveryone = _screenAudio && _iAmSharing;
        bool djForListeners = _djMode && _session?.DjHost == true;
        bool querem = djForListeners || screenForEveryone;
        bool djOnly = djForListeners && !screenForEveryone;

        if (!querem)
        {
            if (_music != null) { _music.Dispose(); _music = null; Log.Write("som do sistema: parou"); }
            return;
        }
        if (_music != null)
        {
            _music.DjOnly = djOnly;
            return;   // ja rodando; so muda o publico
        }

        try
        {
            _music = new MusicShare(_session!) { DjOnly = djOnly };
            _music.Start(_audioTarget);
        }
        catch (Exception ex)
        {
            Log.Write("som do sistema falhou: " + ex.Message);
            ShowBanner("Nao consegui capturar o audio do sistema: " + ex.Message);
            _music = null;
            _djMode = false;
            _session?.SetDjState(_session.DjJoined, false);
        }
    }

    // ─── CINEMA ──────────────────────────────────────────────────────────────

    private void OpenCinema()
    {
        if (_session == null) { ShowBanner("Entra numa sala de voz primeiro."); return; }

        if (_cinemaWindow is { IsDisposed: false }) { _cinemaWindow.Activate(); return; }

        _cinema ??= new CinemaSession(_session, Nick);
        // Quem abre e clica em ESCOLHER VIDEO vira o host; quem so abre, assiste.
        bool isHost = _cinema.State == CinemaSession.Phase.Ocioso
                   || _cinema.HostNick == Nick;

        var view = new CinemaView(_cinema, isHost) { Dock = DockStyle.Fill };
        _cinemaWindow = new Form
        {
            Text = "PRIMICORD — CINEMA",
            ClientSize = new Size(1000, 620),
            MinimumSize = new Size(640, 400),
            BackColor = Pv.Charcoal,
            ForeColor = Pv.Bone,
            Font = Pv.Body,
            StartPosition = FormStartPosition.CenterParent,
        };
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe != null) _cinemaWindow.Icon = Icon.ExtractAssociatedIcon(exe);
        }
        catch { }
        _cinemaWindow.Controls.Add(view);
        _cinemaWindow.FormClosed += (_, _) => _cinemaWindow = null;
        _cinemaWindow.Show(this);
    }

    private void SyncRoomButtons()
    {
        if (_icMic == null) return;

        bool muted = _session?.Muted == true || _voice == null;
        _icMic.Alert = muted;
        _icMic.Caption = muted ? "MUDO" : "MIC";
        _icMic.Invalidate();

        _icShare!.Active = _iAmSharing;
        _icShare.Caption = _iAmSharing ? "NA TELA" : "TELA";
        _icShare.Invalidate();

        bool buffering = _clips?.Active == true;
        _icRec!.Alert = buffering;
        _icRec.Caption = buffering ? "GRAVANDO" : "BUFFER";
        _icRec.Invalidate();

        // A tecla aparece no proprio icone — e assim que o atalho e descoberto.
        var (hkMods, hkKey) = HotkeyBinding.Parse(_cfg.ClipHotkey, HotkeyBinding.Mods.None, Keys.F9);
        _icClip!.Enabled = buffering;
        _icClip.Badge = _clipHotkey.IsRegistered ? HotkeyBinding.Format(hkMods, hkKey) : "";
        _icClip.Invalidate();

        bool djJoined = _session?.DjJoined == true;
        bool djHosting = _session?.DjHost == true;
        _icDj!.Active = djJoined;
        _icDj.Caption = djHosting ? "NO AR" : djJoined ? "OUVINDO" : "DJ";
        _icDj.Invalidate();

        if (_djLabel != null)
        {
            var remoteHost = _session?.Peers.FirstOrDefault(p => p.DjHost);
            string track = djHosting ? _nowPlaying : remoteHost?.DjTrack ?? "";
            bool show = djJoined && track.Length > 0;
            _djLabel.Text = show ? "SALA DJ · TOCANDO AGORA: " + track.ToUpperInvariant() : "";
            _djLabel.Visible = show;
        }

        // Selo de sessao local: avisa que a qualidade esta liberada.
        if (_lanBadge != null)
        {
            bool tailnet = _session?.TailscaleOnly == true;
            bool lan = !tailnet && _screenSender?.LanSession == true;
            _lanBadge.Text = tailnet ? "TAILSCALE · REDE PRIVADA" : lan ? "LAN · QUALIDADE LIBERADA" : "";
            _lanBadge.Visible = tailnet || lan;
        }
    }

    private void StopTimers()
    {
        try { _pollTimer?.Stop(); _pollTimer?.Dispose(); } catch { }
        _pollTimer = null;
        try { _voiceTimer?.Stop(); _voiceTimer?.Dispose(); } catch { }
        _voiceTimer = null;
        try { _stageTimer?.Stop(); _stageTimer?.Dispose(); } catch { }
        _stageTimer = null;
    }

    // ─── ATALHO GLOBAL DO CLIPE ──────────────────────────────────────────────

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowChrome();
        ApplyClipHotkey();
    }

    private static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    private void ApplyWindowChrome()
    {
        // Mantém a moldura nativa (redimensionamento e acessibilidade), mas tira
        // o azul padrão do Windows para que o acampamento inteiro use a mesma
        // paleta de fuligem/osso.
        try
        {
            int dark = 1;
            DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); // immersive dark mode
            int caption = ColorRef(Pv.SurfaceLowest);
            int text = ColorRef(Pv.Bone);
            DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
            DwmSetWindowAttribute(Handle, 36, ref text, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>(Re)registra o atalho lido do config. Avisa se o Windows recusar.</summary>
    private void ApplyClipHotkey()
    {
        if (!IsHandleCreated) return;
        var (mods, key) = HotkeyBinding.Parse(_cfg.ClipHotkey, HotkeyBinding.Mods.None, Keys.F9);
        if (!_clipHotkey.Register(Handle, mods, key))
        {
            ShowBanner($"O atalho {HotkeyBinding.Format(mods, key)} ja esta em uso por outro "
                     + "programa — escolhe outro na engrenagem.");
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (_clipHotkey.Matches(m)) { OnClipHotkey(); return; }
        base.WndProc(ref m);
    }

    /// <summary>
    /// Atalho apertado — normalmente com o jogo em primeiro plano e o app invisivel.
    /// Por isso todo retorno aqui e por SOM + aviso flutuante, nunca por MessageBox
    /// (que roubaria o foco do jogo).
    /// </summary>
    private async void OnClipHotkey()
    {
        if (_clips == null || !_clips.Active)
        {
            Chime.Fail();
            Toast("SEM BUFFER", "Entra numa sala com alguem compartilhando tela.");
            return;
        }

        var result = await _clips.SaveClipAsync(_cfg.ClipSeconds, _voiceRoomName);
        if (!result.Success)
        {
            Chime.Fail();
            Toast(result.Status == ClipSaveStatus.Busy ? "CLIPE EM EXPORTAÇÃO" : "BUFFER VAZIO",
                result.Status == ClipSaveStatus.Busy ? "O clipe anterior ainda está sendo gravado." : "Espera uns segundos de tela antes de clipar.");
            return;
        }
        Chime.Ok();
        Toast("CLIPE SALVO", $"últimos {_cfg.ClipSeconds}s · {Path.GetFileName(result.Path)}");
        Log.Write("clipe pelo atalho: " + result.Path);
    }

    private void Toast(string title, string sub)
    {
        try
        {
            _toast ??= new ToastOverlay();
            _toast.Show(title, sub);
        }
        catch (Exception ex) { Log.Write("aviso flutuante falhou: " + ex.Message); }
    }

    private NotifyIcon? _tray;

    /// <summary>
    /// Icone na bandeja: fechar a janela minimiza em vez de sair, pra nao derrubar
    /// a call sem querer. Sair de verdade e pelo menu do icone.
    /// </summary>
    private void EnsureTray()
    {
        if (_tray != null || !_cfg.TrayOnClose) return;
        try
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Abrir o Primicord", null, (_, _) => RestoreFromTray());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Sair de vez", null, (_, _) => { _reallyClosing = true; Close(); });

            _tray = new NotifyIcon
            {
                Text = "Primicord",
                Visible = true,
                ContextMenuStrip = menu,
                Icon = Icon ?? SystemIcons.Application,
            };
            _tray.DoubleClick += (_, _) => RestoreFromTray();
        }
        catch (Exception ex) { Log.Write("bandeja falhou: " + ex.Message); }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private bool _reallyClosing;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Fechar com uma call em andamento so minimiza — derrubar a voz por engano
        // no X e o tipo de coisa que irrita todo mundo na sala.
        if (!_reallyClosing && _cfg.TrayOnClose && e.CloseReason == CloseReason.UserClosing && _me != null)
        {
            e.Cancel = true;
            EnsureTray();
            Hide();
            return;
        }

        try { _tray?.Dispose(); } catch { }
        _tray = null;
        _clipHotkey.Unregister();
        try { _toast?.Dispose(); } catch { }
        StopTimers();
        try { _chat?.ClearPresenceAsync().Wait(1200); } catch { }
        // Libera também a captura de tela, o process loopback e o buffer de replay;
        // fechar a janela não pode deixar um worker de áudio preso em segundo plano.
        try { LeaveVoice(); } catch (Exception ex) { Log.Write("encerramento da call: " + ex.Message); }
        base.OnFormClosing(e);
    }

    private static string Sanitize(string s)
    {
        var chars = s.ToLowerInvariant().Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9').ToArray();
        string outp = new(chars);
        return outp.Length == 0 ? "primitivo" : outp[..Math.Min(12, outp.Length)];
    }
}
