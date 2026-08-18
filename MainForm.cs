using System.Drawing.Drawing2D;

namespace Primicord;

/// <summary>
/// Shell do app, no formato do Discord: rail de canais a esquerda, conversa no
/// meio, membros a direita, painel do usuario (com a engrenagem) no rodape do rail.
/// </summary>
/// <remarks>
/// Layout e todo por DOCK + reposicionamento no Resize, nunca por coordenada fixa
/// calculada do ClientSize no construtor — o Windows escala por DPI e tamanho fixo
/// estoura pra fora da tela.
/// </remarks>
public sealed class MainForm : Form
{
    private readonly Firestore _fs = new();
    private readonly RoomDirectory _dir;
    private readonly RoomSocialService _social;
    private readonly Config _cfg;

    private PrimitivaoUser? _me;
    private ChatService? _chat;
    private string Nick => _me?.Nick ?? _cfg.Nick;

    // voz
    private RoomSession? _session;
    private VoiceEngine? _voice;
    /// <summary>Malha WebRTC quando cfg.UseWebRtc esta ligado; null = voz pela malha UDP.</summary>
    private WebRtcVoiceMesh? _webrtc;
    private string _voiceRoomId = "";
    private string _voiceRoomName = "";

    // camera
    private WebcamCapture? _cam;
    private readonly WebcamWall _cams = new();
    private bool _camOn;

    /// <summary>
    /// Minha propria camera dentro da parede, na mesma convencao que o palco ja usa
    /// pra "sou eu" (_focusedSharer == 0).
    /// </summary>
    /// <remarks>
    /// A previa local passa pela MESMA parede das dos outros de proposito. Antes ela
    /// era um Image proprio, trocado e DESCARTADO a cada quadro pela thread da
    /// camera — e a UI, que ja tinha a referencia, pintava um objeto morto. O
    /// WinForms responde a excecao no OnPaint desenhando um X vermelho no lugar do
    /// controle. A parede reusa um bitmap por pessoa e nunca descarta no meio.
    /// </remarks>
    private const uint MyCamId = 0;

    // tela, clipe e DJ
    private ScreenSender? _screenSender;
    private readonly ScreenReceiver _screens = new();
    private ClipRecorder? _clips;
    private MusicShare? _music;
    private StageView? _stage;
    private uint _focusedSharer;          // 0 = minha propria tela
    private bool _iAmSharing;
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

    private Panel? _rail, _brand, _railList, _voiceStrip, _userPanel, _membersList, _contentHost;
    private Panel? _rightPanel, _membersHead, _championshipPanel, _roomRankingPanel;
    private ClassificacaoView? _tabela;
    private ClassificacaoView? _roomTabela;
    private Label? _tabelaHead;
    private ApostasView? _apostas;
    private Label? _apostasHead;
    private ChatView? _chatView;
    private ChatView? _roomChatView;
    private PrimitivaoView? _primitivao;
    private Panel? _roomPanel;
    private SocialArena? _arena;
    private RoomHeader? _roomHeader;
    private RoomActivityView? _roomActivity;
    private readonly Dictionary<uint, PeerTile> _peerTiles = new();
    private PeerTile? _myTile;
    private Label? _roomStatus;
    private bool _roomChatVisible = true;
    private bool _chatAutoCollapsed;
    private HashSet<string> _roomKnownPeers = new(StringComparer.OrdinalIgnoreCase);

    // estado da navegacao: "geral" | "dm:<nick>" | "room:<id>"
    // O centro nasce no Primitivao, e nao mais no chat. Quem esta junto aqui esta
    // FALANDO — o canal de texto ocupando a tela inteira era espaco caro gasto com
    // o que menos se usa. Ele continua a um clique no rail.
    private string _view = "primitivao";
    private List<RoomInfo> _rooms = new();
    private List<string> _members = new();
    private Dictionary<string, MemberPresence> _presence = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _openDms = new();

    private System.Windows.Forms.Timer? _pollTimer, _voiceTimer;
    private int _pollTick;
    private bool _polling;
    private DateTimeOffset _pollBackoffUntil;
    private int _pollBackoffLevel;
    private bool _serverBanner;

    public MainForm()
    {
        _dir = new RoomDirectory(_fs);
        _social = new RoomSocialService(_fs);
        _cfg = Config.Load();

        Text = "PRIMICORD";
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe != null) Icon = Icon.ExtractAssociatedIcon(exe);
        }
        catch (Exception ex) { Log.Write("icone da janela: " + ex.Message); }

        ClientSize = new Size(1100, 700);
        MinimumSize = new Size(880, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Pv.Charcoal;
        ForeColor = Pv.Bone;
        Font = Pv.Body;
        DoubleBuffered = true;

        Controls.Add(_body);
        Controls.Add(_banner);

        ShowLogin();
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

    private bool ServerBackoffActive => DateTimeOffset.UtcNow < _pollBackoffUntil;

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

        var host = new Panel { BackColor = Pv.Charcoal };
        var card = new Panel { Size = new Size(410, 356), BackColor = Pv.Char2 };
        card.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var p = new Pen(Pv.Char3, 2)) g.DrawRectangle(p, 0, 0, card.Width - 1, card.Height - 1);
            using (var b = new SolidBrush(Pv.Orange))
                Pv.DrawTracked(g, "PRIMICORD", Pv.Display, b, 24, 22, 2.5f);
        };

        var hint = new Label
        {
            Text = "Entra com a mesma conta do site do Primitivao.",
            Font = Pv.Body, ForeColor = Pv.BoneDim, Location = new Point(24, 62), AutoSize = true,
        };

        var lblNick = SectionLabel("NICK");
        lblNick.Location = new Point(24, 100);
        _loginNick = new PrimInput("seu nick")
        {
            Location = new Point(24, 120), Width = 362,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        _loginNick.Value = presetNick ?? _cfg.Nick;

        var lblPass = SectionLabel("SENHA");
        lblPass.Location = new Point(24, 170);
        _loginPass = new PrimInput("sua senha")
        {
            Location = new Point(24, 190), Width = 362,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        _loginPass.Box.UseSystemPasswordChar = true;

        _loginErr = new Label
        {
            Text = "", Font = Pv.Body, ForeColor = Pv.Red, Location = new Point(24, 236),
            Size = new Size(362, 44), AutoSize = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        _loginBtn = new PrimButton("ENTRAR")
        {
            Location = new Point(24, 288), Size = new Size(362, 44),
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
        if (!res.Ok)
        {
            string hash = Primitivao.HashPassword(_loginPass.Value);
            if (res.Transient && TryCachedLogin(_loginNick.Value, hash, res)) return;
            SetLoginBusy(false, res.Error ?? "Nao consegui entrar");
            return;
        }
        OnLoggedIn(res.User!);
    }

    private async Task TryAutoLoginAsync()
    {
        SetLoginBusy(true, "");
        var res = await Primitivao.AuthenticateWithHashAsync(_fs, _cfg.Nick, _cfg.SenhaHash);
        if (res.Ok) { OnLoggedIn(res.User!); return; }
        Log.Write("auto-login falhou: " + res.Error);
        if (res.Transient && TryCachedLogin(_cfg.Nick, _cfg.SenhaHash, res)) return;
        SetLoginBusy(false, res.Error ?? "");
    }

    private bool TryCachedLogin(string nick, string hash, Primitivao.AuthResult failure)
    {
        var cached = Primitivao.AuthenticateCached(_cfg, nick, hash);
        if (cached == null) return false;

        int seconds = failure.QuotaExceeded ? 15 * 60 : 30;
        _pollBackoffLevel = 1;
        _pollBackoffUntil = DateTimeOffset.UtcNow.AddSeconds(seconds);
        Log.Write($"login pelo perfil salvo; nova tentativa do servidor em {seconds}s");
        OnLoggedIn(cached, cacheProfile: false);
        _serverBanner = true;
        ShowBanner((failure.Error ?? "Servidor indisponivel") +
                   $" Perfil salvo ativo; nova tentativa em {(seconds >= 60 ? seconds / 60 + " min" : seconds + " s")}.");
        return true;
    }

    private void OnLoggedIn(PrimitivaoUser user, bool cacheProfile = true)
    {
        if (InvokeRequired) { BeginInvoke(() => OnLoggedIn(user, cacheProfile)); return; }
        _me = user;
        _cfg.Nick = user.Nick;
        _cfg.SenhaHash = user.SenhaHash;
        if (cacheProfile) _cfg.CacheProfile(user);
        _cfg.Save();
        _chat = new ChatService(_fs, user.Nick);

        // Adota o tema escolhido no site (so leitura — trocar continua sendo la).
        Pv.SetAccent(_cfg.UseSiteTheme ? user.ThemeAccent : null);
        if (_cfg.UseSiteTheme && user.ThemeAccent != null)
            Log.Write($"tema do site aplicado: {user.ThemeId}");

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

        // ── RAIL ESQUERDO ──
        _rail = new Panel { Dock = DockStyle.Left, Width = 236, BackColor = Pv.Char2 };
        _rail.Paint += (_, e) =>
        {
            using var p = new Pen(Pv.Char3, 2);
            e.Graphics.DrawLine(p, _rail.Width - 1, 0, _rail.Width - 1, _rail.Height);
        };

        _brand = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = Pv.Char2 };
        _brand.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var b = new SolidBrush(Pv.Orange))
                Pv.DrawTracked(g, "PRIMICORD", Pv.DisplaySm, b, 16, 18, 2.2f);
            using (var p = new Pen(Pv.Char3, 2)) g.DrawLine(p, 0, _brand.Height - 1, _brand.Width, _brand.Height - 1);
        };

        _userPanel = BuildUserPanel();
        _voiceStrip = BuildVoiceStrip();
        _railList = new Panel
        {
            Dock = DockStyle.Fill, AutoScroll = true, BackColor = Pv.Char2,
            Padding = new Padding(0, 8, 0, 8),
        };

        _roomRankingPanel = BuildRoomRankingPanel();
        _roomRankingPanel.Visible = false;

        _rail.Controls.Add(_railList);   // Fill primeiro
        _rail.Controls.Add(_roomRankingPanel);
        _rail.Controls.Add(_voiceStrip);
        _rail.Controls.Add(_userPanel);
        _rail.Controls.Add(_brand);

        // ── MEMBROS (direita) ──
        _rightPanel = new Panel { Dock = DockStyle.Right, Width = 212, BackColor = Pv.Char2 };
        _rightPanel.Paint += (_, e) =>
        {
            using var p = new Pen(Pv.Char3, 2);
            e.Graphics.DrawLine(p, 0, 0, 0, _rightPanel.Height);
        };
        _membersHead = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Pv.Char2 };
        _membersHead.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using var b = new SolidBrush(Pv.BoneDim);
            Pv.DrawTracked(g, "MEMBROS", Pv.Label, b, 18, 22, 2.4f);
        };
        _membersList = new Panel
        {
            Dock = DockStyle.Fill, AutoScroll = true, BackColor = Pv.Char2,
            Padding = new Padding(0, 4, 0, 8),
        };
        // ── CLASSIFICACAO (embaixo, na mesma coluna) ──
        _championshipPanel = new Panel { Dock = DockStyle.Bottom, AutoSize = true, BackColor = Pv.Char2 };
        _tabelaHead = new Label
        {
            Dock = DockStyle.Top, Height = 34, BackColor = Pv.Char2, ForeColor = Pv.BoneDim,
            Font = Pv.Label, Padding = new Padding(18, 12, 8, 0), Text = "LOL",
        };
        _tabela = new ClassificacaoView { Dock = DockStyle.Top };

        _apostasHead = new Label
        {
            Dock = DockStyle.Top, Height = 30, BackColor = Pv.Char2, ForeColor = Pv.BoneDim,
            Font = Pv.Label, Padding = new Padding(18, 10, 8, 0), Text = "APOSTAS DISPONIVEIS",
            Visible = false,
        };
        _apostas = new ApostasView { Dock = DockStyle.Top, Visible = false };

        // Ordem inversa: o ultimo adicionado fica no topo da pilha do Dock.
        _championshipPanel.Controls.Add(_apostas);
        _championshipPanel.Controls.Add(_apostasHead);
        _championshipPanel.Controls.Add(_tabela);
        _championshipPanel.Controls.Add(_tabelaHead);
        _championshipPanel.Paint += (_, e) =>
        {
            using var p = new Pen(Pv.Char3, 2);
            e.Graphics.DrawLine(p, 12, 0, _championshipPanel.Width - 12, 0);
        };

        _roomChatView = new ChatView(compact: true) { Dock = DockStyle.Fill, Visible = false };
        _roomChatView.Send += OnSendMessageAsync;
        _roomActivity = new RoomActivityView { Visible = false };

        // Fill primeiro, depois as bordas: o WinForms encaixa na ordem inversa.
        _rightPanel.Controls.Add(_membersList);
        _rightPanel.Controls.Add(_roomChatView);
        _rightPanel.Controls.Add(_roomActivity);
        _rightPanel.Controls.Add(_championshipPanel);
        _rightPanel.Controls.Add(_membersHead);

        // ── CONTEUDO ──
        _contentHost = new Panel { Dock = DockStyle.Fill, BackColor = Pv.Charcoal };

        host.Controls.Add(_contentHost);   // Fill primeiro
        host.Controls.Add(_rightPanel);
        host.Controls.Add(_rail);
        SetBody(host);

        _chatView = new ChatView { Dock = DockStyle.Fill };
        _chatView.Send += OnSendMessageAsync;

        SelectView("primitivao");
        RebuildRail();

        // O shell nao precisa reler sala, presenca e chat a cada 2s. A voz tem seu
        // proprio ciclo enquanto esta numa call; aqui 10s e suficiente e preserva
        // a cota compartilhada do Firestore.
        _pollTimer = new System.Windows.Forms.Timer { Interval = 10_000 };
        _pollTimer.Tick += async (_, _) => await PollAsync();
        _pollTimer.Start();
        _ = PollAsync();
    }

    private Panel BuildUserPanel()
    {
        var p = new Panel { Dock = DockStyle.Bottom, Height = 62, BackColor = Pv.Char3 };
        p.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            if (_me == null) return;

            var box = new Rectangle(12, (p.Height - 34) / 2, 34, 34);
            Glyphs.Avatar(g, box, _me.Nick, Pv.Orange, Pv.Charcoal);
            Glyphs.StatusDot(g, new RectangleF(box.Right - 10, box.Bottom - 10, 12, 12), Pv.Green, Pv.Char3);

            using (var b = new SolidBrush(Pv.Bone))
                g.DrawString(_me.Nick, Pv.BodyBold, b, box.Right + 10, box.Y + 1);
            string sub = _me.Badge.Length > 0 ? _me.Badge : _me.PcShort + " PC";
            using (var b = new SolidBrush(_me.Badge.Length > 0 ? Pv.Orange : Pv.BoneDim))
                g.DrawString(sub, Pv.Label, b, box.Right + 10, box.Y + 19);
        };

        var gear = new GlyphButton(Glyphs.Gear) { Size = new Size(34, 34) };
        gear.ToolTipText = "Configuracoes de som";
        gear.Click += (_, _) => OpenSettings();
        var logout = new GlyphButton(Glyphs.Exit) { Size = new Size(34, 34) };
        logout.ToolTipText = "Trocar de conta";
        logout.Click += (_, _) => Logout();

        void Layout()
        {
            gear.Location = new Point(p.ClientSize.Width - 44, (p.Height - gear.Height) / 2);
            logout.Location = new Point(p.ClientSize.Width - 82, (p.Height - logout.Height) / 2);
        }
        p.Resize += (_, _) => Layout();
        p.Controls.AddRange(new Control[] { gear, logout });
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
            using (var pen = new Pen(Pv.Char3, 2)) g.DrawLine(pen, 0, 0, p.Width, 0);
            using (var b = new SolidBrush(Pv.Green))
                Pv.DrawTracked(g, "VOZ CONECTADA", Pv.Label, b, 14, 10, 1.6f);
            using (var b = new SolidBrush(Pv.Bone))
                g.DrawString(_voiceRoomName, Pv.Body, b, 14, 24);
        };
        return p;
    }

    private Panel BuildRoomRankingPanel()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Bottom, Height = 194, BackColor = Color.FromArgb(31, 26, 22),
            Padding = new Padding(8, 0, 8, 8),
        };
        panel.Paint += (_, e) =>
        {
            using var line = new Pen(Pv.Char3, 1);
            e.Graphics.DrawLine(line, 10, 0, panel.Width - 10, 0);
        };

        var head = new Label
        {
            Dock = DockStyle.Top, Height = 34, Text = "RANKING GERAL",
            Font = Pv.Label, ForeColor = Pv.Red, BackColor = panel.BackColor,
            Padding = new Padding(8, 12, 0, 0),
        };
        _roomTabela = new ClassificacaoView
        {
            Dock = DockStyle.Top, MaxLinhas = 5, BackColor = panel.BackColor,
        };
        var complete = new PrimButton("VER RANKING COMPLETO", PrimButton.Style.Ghost)
        {
            Dock = DockStyle.Bottom, Height = 31,
        };
        complete.Click += (_, _) => SelectView("primitivao");

        panel.Controls.Add(_roomTabela);
        panel.Controls.Add(complete);
        panel.Controls.Add(head);
        return panel;
    }

    private void UpdateVoiceStrip()
    {
        if (_voiceStrip == null) return;
        bool on = _session != null && !_view.StartsWith("room:");
        _voiceStrip.Visible = on;
        _voiceStrip.Height = on ? 92 : 0;
        _voiceStrip.Controls.Clear();
        if (!on) return;

        var mute = new GlyphButton((g, r, c, w) => Glyphs.Mic(g, r, c, _session!.Muted))
        { Size = new Size(34, 34), Location = new Point(14, 48) };
        mute.Accent = _session!.Muted ? Pv.Red : Pv.Bone;
        mute.ToolTipText = _session.Muted ? "Desmutar" : "Mutar";
        mute.Click += (_, _) => { ToggleMute(); UpdateVoiceStrip(); };

        var leave = new GlyphButton(Glyphs.Exit) { Size = new Size(34, 34), Location = new Point(54, 48) };
        leave.Accent = Pv.Red;
        leave.ToolTipText = "Sair da call";
        leave.Click += (_, _) => LeaveVoice();

        _voiceStrip.Controls.AddRange(new Control[] { mute, leave });
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
                _screenSender.TotalUploadBudget = Math.Clamp(_cfg.ScreenBudgetKb, 200, 6000) * 1000;
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
                _voice = new VoiceEngine
                {
                    PreprocessMic = _cfg.EchoCancel,
                    MicAutoGain = _cfg.MicAutoGain,
                };
                _voice.OutputMuted = _audioMuted;
                _voice.Failed += ShowBanner;
                // Reata no transporte que ja estava valendo — trocar de microfone
                // nao pode renegociar as conexoes WebRTC.
                _voice.AttachTransport((IVoiceTransport?)_webrtc ?? _session, _session);
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

        if (_view.StartsWith("room:"))
        {
            string activeId = _view[5..];
            items.Add(RoomRailHeader());
            foreach (var room in _rooms.OrderByDescending(r => r.Id == activeId)
                                       .ThenByDescending(r => r.Count)
                                       .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                bool active = room.Id == activeId;
                string badge = room.Count >= 8 ? "LOTADA"
                    : active || room.Count > 0 ? "AO VIVO" : "ESPERANDO";
                var it = new RailItem(room.Name, RailItem.Kind.Voice)
                {
                    Dock = DockStyle.Top, Height = 48, Active = active,
                    Detail = $"{room.Count}/8", Badge = badge,
                };
                string id = room.Id, name = room.Name;
                it.Click += async (_, _) => await OnRoomClickedAsync(id, name);
                items.Add(it);

                if (!active) continue;
                foreach (string occupant in room.Occupants)
                {
                    bool me = string.Equals(occupant, Nick, StringComparison.OrdinalIgnoreCase);
                    var person = new RailItem(occupant, RailItem.Kind.Dm)
                    {
                        Dock = DockStyle.Top, Height = 31, AvatarNick = occupant,
                        Online = true, Suffix = me ? "VOCE" : "",
                    };
                    string target = occupant;
                    if (!me) person.Click += (_, _) => OpenDm(target);
                    items.Add(person);
                }
            }

            items.Reverse();
            foreach (var c in items) _railList.Controls.Add(c);
            _railList.ResumeLayout();
            return;
        }

        items.Add(RailHeader("PRIMITIVAO"));
        var prim = new RailItem("Campeonato", RailItem.Kind.Action)
        { Dock = DockStyle.Top, Active = _view == "primitivao" };
        prim.Click += (_, _) => SelectView("primitivao");
        items.Add(prim);

        items.Add(RailHeader("CANAIS"));
        var geral = new RailItem("geral", RailItem.Kind.TextChannel)
        { Dock = DockStyle.Top, Active = _view == "geral" };
        geral.Click += (_, _) => SelectView("geral");
        items.Add(geral);

        items.Add(RailHeader("SALAS DE VOZ"));
        foreach (var room in _rooms)
        {
            var it = new RailItem(room.Name, RailItem.Kind.Voice)
            {
                Dock = DockStyle.Top,
                Active = _view == "room:" + room.Id,
                Suffix = $"{room.Count}/8",
            };
            string id = room.Id, name = room.Name;
            it.Click += async (_, _) => await OnRoomClickedAsync(id, name);
            items.Add(it);

            // A sala aberta mostra quem esta dentro, como o painel da referencia.
            // Os dados ja vieram no mesmo poll da RoomDirectory; zero leitura extra.
            if (_view == "room:" + room.Id)
            {
                foreach (string occupant in room.Occupants)
                {
                    bool me = string.Equals(occupant, Nick, StringComparison.OrdinalIgnoreCase);
                    var person = new RailItem(occupant, RailItem.Kind.Dm)
                    {
                        Dock = DockStyle.Top, Height = 30, AvatarNick = occupant,
                        Online = true, Suffix = me ? "VOCE" : "",
                    };
                    string target = occupant;
                    if (!me) person.Click += (_, _) => OpenDm(target);
                    items.Add(person);
                }
            }
        }
        var novaSala = new RailItem("Nova sala", RailItem.Kind.Action) { Dock = DockStyle.Top };
        novaSala.Click += async (_, _) => await CreateRoomAsync();
        items.Add(novaSala);

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

    private Panel RoomRailHeader()
    {
        var panel = new Panel { Dock = DockStyle.Top, Height = 43, BackColor = Pv.Char2 };
        var title = new Label
        {
            Text = "SALAS", Font = Pv.Label, ForeColor = Pv.Red, AutoSize = true,
            Location = new Point(10, 17),
        };
        var create = new PrimButton("+ CRIAR SALA", PrimButton.Style.Ghost)
        {
            Size = new Size(84, 26), Location = new Point(94, 8),
        };
        create.Click += async (_, _) => await CreateRoomAsync();
        panel.Resize += (_, _) => create.Left = panel.ClientSize.Width - create.Width - 8;
        panel.Controls.AddRange(new Control[] { title, create });
        return panel;
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

        if (view.StartsWith("room:"))
        {
            _contentHost.Controls.Add(EnsureRoomPanel());
        }
        else if (view == "primitivao")
        {
            _primitivao ??= new PrimitivaoView { Dock = DockStyle.Fill };
            _primitivao.MeuNick = Nick;
            _contentHost.Controls.Add(_primitivao);
            if (!ServerBackoffActive) _ = RefreshCampeonatoAsync();
        }
        else
        {
            if (_chatView == null) return;
            _contentHost.Controls.Add(_chatView);
            if (view == "geral")
                _chatView.SetHeader("# geral", "O canal de todo mundo.");
            else
            {
                string other = view[3..];
                _chatView.SetHeader("@ " + other,
                    "Conversa direta — nao e criptografada, evita segredo aqui.");
            }
            _chatView.SetMessages(new List<ChatMessage>(), Nick);
            _chatView.FocusComposer();
            if (!ServerBackoffActive) _ = RefreshChatAsync();
        }
        _contentHost.ResumeLayout();
        UpdateRightContext();
        RebuildRail();
    }

    /// <summary>
    /// Na sala, a coluna direita vira CHAT DA SALA e o ranking continua compacto
    /// embaixo. Fora dela, volta a lista global de membros.
    /// </summary>
    private void UpdateRightContext()
    {
        if (_rightPanel == null || _membersList == null || _membersHead == null ||
            _roomChatView == null) return;

        bool inRoom = _view.StartsWith("room:");
        if (_rail != null) _rail.Width = inRoom ? 188 : 236;
        if (_brand != null) _brand.Visible = !inRoom;
        if (_userPanel != null) _userPanel.Visible = !inRoom;
        if (_voiceStrip != null)
        {
            _voiceStrip.Visible = !inRoom && _session != null;
            _voiceStrip.Height = _voiceStrip.Visible ? 92 : 0;
        }
        if (_roomRankingPanel != null) _roomRankingPanel.Visible = inRoom;

        _membersList.Visible = !inRoom;
        _membersHead.Visible = !inRoom;
        _roomChatView.Visible = inRoom && _roomChatVisible;
        if (_roomActivity != null) _roomActivity.Visible = inRoom && _roomChatVisible;
        if (_championshipPanel != null) _championshipPanel.Visible = !inRoom;
        _rightPanel.Width = inRoom ? 258 : 212;
        _rightPanel.Visible = !inRoom || _roomChatVisible;

        if (inRoom)
        {
            _roomChatView.SetHeader("CHAT DA SALA", _voiceRoomName);
            _roomChatView.ComposerPlaceholder = "Mensagem para a sala...";
            _roomHeader?.Invalidate();
            if (!ServerBackoffActive) _ = RefreshChatAsync();
        }
    }

    private void ToggleRoomChat()
    {
        _roomChatVisible = !_roomChatVisible;
        UpdateRightContext();
        if (_icChat != null)
        {
            _icChat.Active = _roomChatVisible;
            _icChat.Invalidate();
        }
    }

    private async Task OnRoomClickedAsync(string roomId, string roomName)
    {
        if (ServerBackoffActive && _voiceRoomId != roomId)
        {
            ShowBanner("Servidor em recuo temporario; nao da para entrar em outra sala agora.");
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
        if (ServerBackoffActive)
        {
            ShowBanner("Servidor em recuo temporario; a mensagem nao foi enviada.");
            return;
        }
        try
        {
            if (_view == "geral") await _chat.SendToChannelAsync(ChatService.GeneralChannel, text);
            else if (_view.StartsWith("dm:")) await _chat.SendDmAsync(_view[3..], text);
            else if (_view.StartsWith("room:"))
                await _chat.SendToChannelAsync(ChatService.RoomChannel(_view[5..]), text);
            await RefreshChatAsync(propagateFirestore: true);
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
        if (DateTimeOffset.UtcNow < _pollBackoffUntil) return;
        _polling = true;
        try
        {
            _pollTick++;
            // Ao sair de um recuo, força um ciclo completo. Sem isso uma tela que
            // nao usa chat poderia "se recuperar" sem fazer nenhuma leitura real.
            bool first = _pollTick == 1 || _pollBackoffLevel > 0;

            // Heartbeat global a cada minuto. A presenca P2P da sala tem ciclo
            // separado; escrever aqui a cada 20s triplicava custo sem melhorar voz.
            if (first || _pollTick % 6 == 1)
                try { await _chat.HeartbeatAsync(_voiceRoomId.Length > 0 ? _voiceRoomName : ""); }
                catch (FirestoreException ex) when (ex.IsPermissionDenied)
                {
                    ShowBanner("O Firestore recusou a escrita — falta publicar as rules no Console.");
                    _pollTimer?.Stop();
                    return;
                }

            if (_members.Count == 0) _members = await Primitivao.ListMembersAsync(_fs);

            // Classificacao: a cada ~5min. Placar de
            // futebol nao muda em dois segundos, e o doc, por menor que seja, e
            // uma leitura a mais por ciclo pra todo mundo que estiver com o app
            // aberto. O primeiro tick busca na hora pra tabela nao nascer vazia.
            if (first || _pollTick % 30 == 1)
                await RefreshCampeonatoAsync(propagateFirestore: true);

            // Lista global e cara porque cada pessoa e um documento. Dois minutos
            // equilibram presenca util e a cota compartilhada.
            if (first || _pollTick % 12 == 1)
            {
                _presence = await _chat.ReadPresenceAsync();
                RefreshMembers();
            }

            // Lobby a cada dois minutos e SEM o antigo N+1 de peers por sala. Os
            // ocupantes das outras salas saem da presenca global; a sala atual e
            // atualizada imediatamente pela RoomSession.
            if (first || _pollTick % 12 == 1)
            {
                var rooms = await _dir.ListAsync(includeOccupants: false);
                foreach (var room in rooms)
                    foreach (var presence in _presence.Values)
                        if (presence.Online && string.Equals(presence.Room, room.Name,
                                                             StringComparison.OrdinalIgnoreCase))
                            room.Occupants.Add(presence.Nick);
                bool roomsChanged = rooms.Count != _rooms.Count ||
                    rooms.Zip(_rooms).Any(t => t.First.Id != t.Second.Id ||
                                               t.First.Count != t.Second.Count);
                _rooms = rooms;
                if (roomsChanged || first) RebuildRail();
            }

            // O cache incremental do chat busca so mensagens posteriores ao cursor.
            // Fora de canal/DM/sala, nao consulta conversa ficticia nenhuma.
            await RefreshChatAsync(propagateFirestore: true);
            ClearServerBackoff();
        }
        catch (FirestoreException ex) when (ex.IsPermissionDenied)
        {
            ShowBanner("O Firestore recusou a leitura — falta publicar as rules no Firebase Console.");
            _pollTimer?.Stop();
        }
        catch (FirestoreException ex)
        {
            ApplyServerBackoff(ex.IsQuotaExceeded, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            ApplyServerBackoff(quota: false, ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            ApplyServerBackoff(quota: false, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Write("poll falhou: " + ex.Message);
            ApplyServerBackoff(quota: false, ex.Message);
        }
        finally { _polling = false; }
    }

    private void ApplyServerBackoff(bool quota, string reason)
    {
        _pollBackoffLevel = Math.Min(5, _pollBackoffLevel + 1);
        int seconds = quota
            ? Math.Min(3600, 15 * 60 * (1 << Math.Min(2, _pollBackoffLevel - 1)))
            : Math.Min(300, 30 * (1 << Math.Min(3, _pollBackoffLevel - 1)));
        _pollBackoffUntil = DateTimeOffset.UtcNow.AddSeconds(seconds);
        _serverBanner = true;
        string wait = seconds >= 60 ? seconds / 60 + " min" : seconds + " s";
        string text = quota
            ? $"Limite de leituras do servidor atingido. Modo offline; nova tentativa em {wait}."
            : $"Servidor temporariamente indisponivel. Nova tentativa em {wait}.";
        ShowBanner(text);
        Log.Write($"poll em recuo por {seconds}s: {reason}");
    }

    private void ClearServerBackoff()
    {
        if (_pollBackoffLevel == 0 && !_serverBanner) return;
        _pollBackoffLevel = 0;
        _pollBackoffUntil = default;
        if (!_serverBanner) return;
        _serverBanner = false;
        _banner.Visible = false;
        _banner.Height = 0;
    }

    /// <summary>Le a tabela do Primitivao e joga na coluna da direita.</summary>
    private async Task RefreshCampeonatoAsync(bool propagateFirestore = false)
    {
        if (_tabela == null || _tabela.IsDisposed) return;
        try
        {
            var painel = await CampeonatoLol.LerAsync(_fs, Nick);
            if (_tabela.IsDisposed) return;

            if (_primitivao != null && !_primitivao.IsDisposed)
            {
                _primitivao.MeuNick = Nick;
                _primitivao.Definir(painel);
            }

            _tabela.MeuNick = Nick;
            _tabela.MaxLinhas = 4;   // o TOP 4, como no rascunho
            _tabela.Definir(painel?.Tabela);
            if (_roomTabela != null && !_roomTabela.IsDisposed)
            {
                _roomTabela.MeuNick = Nick;
                _roomTabela.MaxLinhas = 5;
                _roomTabela.Definir(painel?.Tabela);
            }
            if (_tabelaHead != null && !_tabelaHead.IsDisposed)
            {
                string resumo = _tabela.Resumo;
                _tabelaHead.Text = resumo.Length > 0 ? "LOL  ·  " + resumo : "LOL";
            }

            if (_apostas != null && !_apostas.IsDisposed)
            {
                var abertas = painel?.Apostas ?? new List<ApostaLol>();
                _apostas.Definir(abertas);
                if (_apostasHead != null && !_apostasHead.IsDisposed)
                    _apostasHead.Visible = abertas.Count > 0;
            }
        }
        catch (FirestoreException ex)
        {
            if (propagateFirestore) throw;
            Log.Write("campeonato: " + ex.Message);
        }
        catch (Exception ex) { Log.Write("campeonato: " + ex.Message); }
    }

    private async Task RefreshChatAsync(bool propagateFirestore = false)
    {
        if (_chat == null || _chatView == null || _chatView.IsDisposed) return;
        if (!_view.StartsWith("room:") && _view != "geral" && !_view.StartsWith("dm:")) return;
        try
        {
            if (_view.StartsWith("room:"))
            {
                if (_roomChatView == null || _roomChatView.IsDisposed) return;
                var roomMsgs = await _chat.ReadChannelAsync(ChatService.RoomChannel(_view[5..]));
                if (!_roomChatView.IsDisposed) _roomChatView.SetMessages(roomMsgs, Nick);
            }
            else
            {
                var msgs = _view == "geral"
                    ? await _chat.ReadChannelAsync(ChatService.GeneralChannel)
                    : await _chat.ReadDmAsync(_view[3..]);
                if (!_chatView.IsDisposed) _chatView.SetMessages(msgs, Nick);
            }
        }
        catch (FirestoreException ex)
        {
            if (propagateFirestore) throw;
            Log.Write("ler chat falhou: " + ex.Message);
        }
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
        if (ServerBackoffActive)
        {
            ShowBanner("Servidor em recuo temporario; nao da para criar sala agora.");
            return;
        }
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

        _roomHeader = new RoomHeader
        {
            RoomName = _voiceRoomName,
            Participants = Math.Max(1, (_session?.Peers.Count ?? 0) + 1),
        };

        _roomStatus = new Label
        {
            Height = 0, Visible = false, Text = "conectando...",
        };
        // "tocando agora" vive na linha de status, que tem a largura toda — na barra
        // de botoes ele era cortado pelo painel de membros.
        _djLabel = new Label
        {
            Dock = DockStyle.Top, Height = 22, Font = Pv.Label, ForeColor = Pv.Green,
            Padding = new Padding(22, 2, 0, 0), Text = "", Visible = false,
        };

        // Ferramentas ficam no cabeçalho; embaixo sobram apenas as quatro ações
        // essenciais da call. Isso devolve quase 70px de altura para a arena.
        var actions = BuildRoomActions(out var tools);
        _roomHeader.Controls.Add(tools);

        _arena = new SocialArena { Dock = DockStyle.Fill, RoomName = _voiceRoomName };
        _arena.OwnPositionChanged += OnOwnSocialPositionChanged;
        _stage = new StageView { Visible = false };
        _arena.AttachStage(_stage);

        _roomPanel.Controls.Add(_arena);      // Fill primeiro
        _roomPanel.Controls.Add(actions);
        _roomPanel.Controls.Add(_djLabel);
        _roomPanel.Controls.Add(_roomStatus);
        _roomPanel.Controls.Add(_roomHeader);
        return _roomPanel;
    }

    private ActionIcon? _icMic, _icAudio, _icInvite, _icCam, _icShare, _icRec,
        _icClip, _icDj, _icCinema, _icQuick, _icChat, _icLeave;
    private bool _audioMuted;
    private Label? _lanBadge;
    private CinemaSession? _cinema;
    private Form? _cinemaWindow;

    /// <summary>
    /// Barra de icones acima dos participantes. Antes eram botoes de texto largos:
    /// cinco rotulos por extenso somavam ~800px e o ultimo saia da tela.
    /// </summary>
    private Panel BuildRoomActions(out FlowLayoutPanel tools)
    {
        var host = new Panel
        {
            Dock = DockStyle.Bottom, Height = 76, BackColor = Color.FromArgb(8, 8, 8),
        };

        tools = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, Width = 320, Height = 55,
            BackColor = Color.FromArgb(19, 15, 13), WrapContents = false,
            AutoScroll = false, Padding = new Padding(3, 7, 3, 0),
        };

        var card = new Panel { BackColor = Pv.Char2, Height = 62, Padding = new Padding(1) };
        var primary = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Pv.Char2, WrapContents = false,
            AutoScroll = false, Padding = Padding.Empty, Margin = Padding.Empty,
        };
        card.Controls.Add(primary);
        card.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var border = new Pen(Pv.Char3, 1);
            using var path = Pv.RoundRect(new Rectangle(0, 0, card.Width - 1, card.Height - 1), 26);
            e.Graphics.DrawPath(border, path);
            for (int i = 1; i < 4; i++)
            {
                int x = card.Width * i / 4;
                e.Graphics.DrawLine(border, x, 10, x, card.Height - 10);
            }
        };

        _icMic = new ActionIcon((g, r, c, w) => Glyphs.Mic(g, r, c, _session?.Muted == true), "MIC")
        { ToolTipText = "Mutar / desmutar o microfone" };
        _icMic.Click += (_, _) => { ToggleMute(); SyncRoomButtons(); };

        _icAudio = new ActionIcon(Glyphs.Speaker, "AUDIO")
        { ToolTipText = "Silenciar / restaurar o audio da sala" };
        _icAudio.Click += (_, _) => ToggleRoomAudio();

        _icInvite = new ActionIcon(Glyphs.Plus, "CONVIDAR")
        { ToolTipText = "Copiar convite desta sala" };
        _icInvite.Click += (_, _) => CopyRoomInvite();

        _icCam = new ActionIcon((g, r, c, w) => Glyphs.Camera(g, r, c, _camOn), "CAMERA")
        { ToolTipText = "Ligar/desligar a camera" };
        _icCam.Click += (_, _) => ToggleWebcam();

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
        { ToolTipText = "Transmitir o som do sistema pra sala" };
        _icDj.Click += (_, _) => ToggleDj();

        _icCinema = new ActionIcon(Glyphs.Film, "CINEMA")
        { ToolTipText = "Assistir um video junto, na qualidade original" };
        _icCinema.Click += (_, _) => OpenCinema();

        _icQuick = new ActionIcon(Glyphs.Gear, "AJUSTES")
        { ToolTipText = "Microfone, saida e qualidade (sem sair da call)" };
        _icQuick.Click += (_, _) => OpenQuickSettings();

        _icChat = new ActionIcon(Glyphs.Hash, "CHAT")
        { ToolTipText = "Mostrar / esconder o chat da sala", Active = true };
        _icChat.Click += (_, _) => ToggleRoomChat();

        _icLeave = new ActionIcon(Glyphs.Exit, "SAIR") { ToolTipText = "Sair da sala de voz" };
        _icLeave.Click += (_, _) => LeaveVoice();

        _lanBadge = new Label
        {
            Font = Pv.Label, ForeColor = Pv.Green, AutoSize = true, Text = "",
            Location = new Point(8, 3), Visible = false,
        };

        foreach (var ic in new[] { _icCam, _icShare, _icRec, _icClip, _icDj, _icCinema, _icQuick, _icChat })
        {
            ic!.Compact = true;
            ic.Caption = "";
            ic.Size = new Size(39, 42);
            ic.Margin = Padding.Empty;
            ic.BackColor = tools.BackColor;
        }
        foreach (var ic in new[] { _icMic, _icAudio, _icInvite, _icLeave })
        {
            ic!.Wide = true;
            ic.Height = 62;
            ic.Margin = Padding.Empty;
            ic.BackColor = Color.Transparent;
        }
        _icMic.Subtitle = "Ativo";
        _icAudio.Subtitle = "Alto";
        _icInvite.Subtitle = "amigos";
        _icLeave.Subtitle = "voltar ao lobby";

        tools.Controls.AddRange(new Control[]
            { _icCam, _icShare, _icRec, _icClip, _icDj, _icCinema, _icQuick, _icChat });
        primary.Controls.AddRange(new Control[] { _icMic, _icAudio, _icInvite, _icLeave });
        void LayoutPrimary()
        {
            int width = Math.Min(560, Math.Max(360, host.ClientSize.Width - 34));
            card.SetBounds((host.ClientSize.Width - width) / 2, 7, width, 62);
            int each = Math.Max(88, width / 4);
            foreach (var ic in new[] { _icMic, _icAudio, _icInvite, _icLeave }) ic!.Width = each;
        }
        host.Resize += (_, _) => LayoutPrimary();
        LayoutPrimary();
        host.Controls.Add(card);
        host.Controls.Add(_lanBadge);
        return host;
    }

    private void ToggleRoomAudio()
    {
        _audioMuted = !_audioMuted;
        if (_voice != null) _voice.OutputMuted = _audioMuted;
        SyncRoomButtons();
    }

    private void CopyRoomInvite()
    {
        if (_voiceRoomId.Length == 0) return;
        string invite = $"PRIMICORD · {_voiceRoomName}\r\nSala: {_voiceRoomId}";
        try
        {
            Clipboard.SetText(invite);
            ShowBanner("Convite da sala copiado.");
        }
        catch (Exception ex) { Log.Write("copiar convite: " + ex.Message); }
    }

    private void OpenQuickSettings()
    {
        if (_icQuick == null) return;
        var pos = _icQuick.PointToScreen(new Point(_icQuick.Width / 2, 0));
        using var q = new QuickSettings(_cfg, pos);
        q.Applied += () =>
        {
            if (_screenSender != null)
                _screenSender.TotalUploadBudget = Math.Clamp(_cfg.ScreenBudgetKb, 200, 6000) * 1000;
            _screenAudio = _cfg.ShareAudioWithScreen;
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
                PreprocessMic = _cfg.EchoCancel,
                MicAutoGain = _cfg.MicAutoGain,
            };
                    _voice.OutputMuted = _audioMuted;
                    _voice.Failed += ShowBanner;
                    _voice.AttachTransport((IVoiceTransport?)_webrtc ?? _session, _session);
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

        SocialPosition myPosition = SocialPosition.DefaultFor(
            RoomSession.HashId(Nick.ToLowerInvariant()));
        try
        {
            myPosition = await _social.LoadAsync(roomId, Nick) ?? myPosition;
        }
        catch (Exception ex) { Log.Write("posicao social nao carregou: " + ex.Message); }

        string peerId = Sanitize(Nick) + "-" + Random.Shared.Next(0x10000, 0xFFFFF).ToString("x5");
        _session = new RoomSession(_fs, roomId, peerId, Nick);
        _session.UpdateSocial(myPosition);
        _session.PeersChanged += OnPeersChanged;
        _session.SocialMoved += OnPeerSocialMoved;
        _session.Failed += ShowBanner;

        _session.ScreenFrameReceived += OnPeerFrame;
        _session.WebcamFrameReceived += OnPeerCam;

        _voice = new VoiceEngine
            {
                MusicVolume = _cfg.MusicVolume / 100f,
                PreprocessMic = _cfg.EchoCancel,
                MicAutoGain = _cfg.MicAutoGain,
            };
        _voice.Failed += ShowBanner;

        // A malha UDP sobe SEMPRE — ela carrega tela, musica, cinema e presenca.
        // A chave decide so quem leva a VOZ.
        if (_cfg.UseWebRtc)
        {
            _webrtc = new WebRtcVoiceMesh(_fs, roomId, peerId, _session);
            _webrtc.Failed += ShowBanner;
            _webrtc.StateChanged += () =>
            {
                if (!IsDisposed) try { BeginInvoke(UpdateRoomStatus); } catch { }
            };
            _voice.AttachTransport(_webrtc, _session);
        }
        else
        {
            _voice.AttachTransport(_session, _session);
        }

        // Buffer rolante de clipe: recebe o que eu ouço e o meu microfone.
        _clips = new ClipRecorder(60);
        _voice.HeardPcm += (b, o, c) => _clips?.PushHeard(b, o, c);
        _voice.MicPcm += (b, o, c) => _clips?.PushMic(b, o, c);

        // Recria as bolinhas do zero pra esta sala.
        _peerTiles.Clear();
        EnsureRoomPanel();
        _arena!.RoomName = roomName;
        _arena.ClearParticipants();
        if (_roomHeader != null)
        {
            _roomHeader.RoomName = roomName;
            _roomHeader.Participants = 1;
        }
        _roomKnownPeers.Clear();
        _roomActivity?.Reset(roomName);
        var joiningRoom = _rooms.FirstOrDefault(r => r.Id == roomId);
        if (joiningRoom != null && !joiningRoom.Occupants.Contains(Nick, StringComparer.OrdinalIgnoreCase))
            joiningRoom.Occupants.Add(Nick);
        _myTile = new PeerTile { Nick = Nick, IsMe = true, Connected = true };
        _arena.SetOwn(_myTile, myPosition);
        _audioMuted = false;
        _roomChatVisible = ClientSize.Width >= 900;

        UpdateVoiceStrip();
        UpdateRightContext();

        try
        {
            _voice.Start(_cfg.MicDevice,
                string.IsNullOrEmpty(_cfg.OutputDeviceId) ? null : _cfg.OutputDeviceId);
            await _session.StartAsync();
            // Depois da malha: a negociacao WebRTC usa a presenca dela pra saber
            // com quem falar.
            _webrtc?.Start();
            UpdateRoomStatus();
        }
        catch (FirestoreException ex) when (ex.IsPermissionDenied)
        {
            ShowBanner("O Firestore recusou a escrita — falta publicar as rules do pc_rooms no Console.");
        }
        catch (Exception ex)
        {
            Log.Write("entrar na sala falhou: " + ex.Message);
            ShowBanner("Nao consegui entrar: " + ex.Message);
        }

        SyncRoomButtons();

        _voiceTimer?.Stop();
        _voiceTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _voiceTimer.Tick += (_, _) => TickVoice();
        _voiceTimer.Start();
    }

    private void LeaveVoice()
    {
        _voiceTimer?.Stop();
        _voiceTimer?.Dispose();
        _voiceTimer = null;

        try { _cinemaWindow?.Close(); } catch { }
        _cinemaWindow = null;
        try { _cinema?.Dispose(); } catch { }
        _cinema = null;
        _bufferOffByUser = false;

        try { _cam?.Dispose(); } catch { }
        _cam = null;
        _camOn = false;
        _cams.Dispose();

        try { _screenSender?.Dispose(); } catch { }
        _screenSender = null;
        _iAmSharing = false;
        try { _music?.Dispose(); } catch { }
        _music = null;
        try { _clips?.Dispose(); } catch { }
        _clips = null;
        _screens.Dispose();
        _focusedSharer = 0;
        _nowPlaying = "";

        try { _voice?.Dispose(); } catch { }
        // Antes da sessao: a malha WebRTC le a presenca dela pra saber quem saiu.
        try { _webrtc?.Dispose(); } catch { }
        try { _session?.Dispose(); } catch { }
        _voice = null;
        _webrtc = null;
        _session = null;
        var leavingRoom = _rooms.FirstOrDefault(r => r.Id == _voiceRoomId);
        leavingRoom?.Occupants.RemoveAll(n => string.Equals(n, Nick, StringComparison.OrdinalIgnoreCase));
        _voiceRoomId = "";
        _voiceRoomName = "";
        _peerTiles.Clear();
        _roomKnownPeers.Clear();
        _arena?.ClearParticipants();
        _myTile = null;

        UpdateVoiceStrip();
        if (_view.StartsWith("room:")) SelectView("geral");
    }

    private void OnPeersChanged()
    {
        if (InvokeRequired) { BeginInvoke(OnPeersChanged); return; }
        if (_session == null || _arena == null || _arena.IsDisposed) return;

        var peers = _session.Peers;
        var alive = peers.Select(p => p.SenderId).ToHashSet();
        var currentNames = peers.Select(p => p.Nick).Where(n => n.Length > 0)
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string entered in currentNames.Except(_roomKnownPeers, StringComparer.OrdinalIgnoreCase))
            _roomActivity?.Push($"{entered} entrou na sala", "agora");
        foreach (string left in _roomKnownPeers.Except(currentNames, StringComparer.OrdinalIgnoreCase))
            _roomActivity?.Push($"{left} saiu da sala", "agora");
        _roomKnownPeers = currentNames;

        var activeRoom = _rooms.FirstOrDefault(r => r.Id == _voiceRoomId);
        if (activeRoom != null)
        {
            var occupants = currentNames.Append(Nick).Distinct(StringComparer.OrdinalIgnoreCase)
                                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            var old = activeRoom.Occupants.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (!occupants.SequenceEqual(old, StringComparer.OrdinalIgnoreCase))
            {
                activeRoom.Occupants.Clear();
                activeRoom.Occupants.AddRange(occupants);
                RebuildRail();
            }
        }

        foreach (var p in peers)
        {
            if (!_peerTiles.TryGetValue(p.SenderId, out var tile))
            {
                tile = new PeerTile { Margin = new Padding(6) };
                _peerTiles[p.SenderId] = tile;
                _arena.SetParticipant(p.SenderId, tile, p.Social);
            }
            tile.Nick = p.Nick;
            tile.Muted = p.Muted;
            tile.Sharing = p.Sharing;
            tile.Connected = p.Connected;
            tile.Punching = p.Locked == null;
            tile.SilentSeconds = p.SilentSeconds;
            _arena.SetPosition(p.SenderId, p.Social);
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
            _arena.RemoveParticipant(sid);
            _voice?.RemovePeer(sid);
            _screens.Remove(sid);
            _cams.Remove(sid);
            if (_focusedSharer == sid) _focusedSharer = 0;
        }
        UpdateRoomStatus();
    }

    private void OnPeerSocialMoved(uint senderId, SocialPosition position)
    {
        if (InvokeRequired)
        {
            try { BeginInvoke(() => OnPeerSocialMoved(senderId, position)); } catch { }
            return;
        }
        _arena?.SetPosition(senderId, position);
    }

    private void OnOwnSocialPositionChanged(SocialPosition position, bool final)
    {
        var session = _session;
        if (session == null) return;
        session.UpdateSocial(position, final);
        if (!final) return;

        string roomId = _voiceRoomId;
        string nick = Nick;
        _ = _social.SaveAsync(roomId, nick, position).ContinueWith(t =>
        {
            if (t.IsFaulted) Log.Write("posicao social nao salvou: " + t.Exception?.GetBaseException().Message);
        }, TaskScheduler.Default);
    }

    private int _stageTick;

    private void TickVoice()
    {
        if (_voice == null || _session == null) return;

        // Sem compartilhamento, a arena ocupa todo o centro. Com tela, o palco
        // aparece atras das bolinhas — a presenca social nao some durante o jogo.
        if (_stage != null && !_stage.IsDisposed)
        {
            bool showStage = _iAmSharing || _session.Peers.Any(p => p.Sharing);
            if (_stage.Visible != showStage)
            {
                _stage.Visible = showStage;
                _arena?.Invalidate();
            }
        }

        // Cameras: reaponta cada tile pro quadro mais novo. O FrameOf devolve null
        // sozinho quando a pessoa para de mandar, entao desligar a camera do outro
        // lado limpa o tile aqui sem precisar de aviso nenhum pela rede.
        if (_myTile != null && !_myTile.IsDisposed)
        {
            var mine = _cams.FrameOf(MyCamId);
            _myTile.Cam = mine;
            if (mine != null) _myTile.Invalidate();
        }
        foreach (var (sid, tile) in _peerTiles)
        {
            var frame = _cams.FrameOf(sid);
            if (ReferenceEquals(tile.Cam, frame) && frame == null) continue;
            tile.Cam = frame;
            tile.Invalidate();
        }

        // Palco: mostra a tela de quem esta em foco (a minha ja chega por OnMyFrame).
        if (_stage != null && !_stage.IsDisposed && _focusedSharer != 0)
        {
            _stage.SetFrame(_screens.FrameOf(_focusedSharer));
            var who = _session.Peers.FirstOrDefault(p => p.SenderId == _focusedSharer);
            _stage.SharerNick = who?.Nick ?? "";
        }
        else if (_stage != null && !_stage.IsDisposed && _focusedSharer == 0)
        {
            _stage.SharerNick = "";
            _stage.SetFrame(null);
        }

        // Minha propria tela NAO e exibida aqui — ver a si mesmo criava espelho
        // infinito e fazia todo bloco mudar a cada quadro, torrando a banda.
        if (_stage != null && !_stage.IsDisposed)
        {
            bool self = _iAmSharing && _focusedSharer == 0;
            _stage.SelfPreview = self;
            _stage.SelfInfo = self && _screenSender != null
                ? $"{_screenSender.OutWidth}x{_screenSender.OutHeight} · {_screenSender.Fps} FPS · "
                  + $"{_screenSender.KbPerSecond} KB/s"
                : "";
        }

        if (_stage != null && !_stage.IsDisposed)
        {
            bool buffering = _clips?.Active == true;
            _stage.Recording = buffering;
            string net = _iAmSharing && _screenSender != null
                ? $"{_screenSender.Fps}FPS · Q{_screenSender.Quality} · {_screenSender.KbPerSecond}KB/s"
                : "";
            _stage.StatusRight = buffering
                ? $"BUFFER {_clips!.BufferSeconds}s" + (net.Length > 0 ? " · " + net : "")
                : net;
        }

        // "Tocando agora" a cada ~2s quando sou o DJ.
        if (_music != null && ++_stageTick % 20 == 0)
        {
            _ = NowPlaying.ReadAsync().ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully || t.Result == _nowPlaying) return;
                _nowPlaying = t.Result;
                try { BeginInvoke(SyncRoomButtons); } catch { }
            });
        }

        if (_myTile != null && !_myTile.IsDisposed)
        {
            _myTile.Level = _session.Muted ? 0f : _voice.MyPeak;
            _myTile.Muted = _session.Muted;
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

        // Quem ja passou do prazo nao esta "conectando": nao vai conectar. Dizer o
        // que aconteceu, com o que fazer, em vez de girar pra sempre — o README
        // sempre prometeu esse aviso, e ate agora ele nao existia.
        const int DesisteSegundos = 25;
        int semRota = peers.Count(p => p.Locked == null && p.SilentSeconds >= DesisteSegundos);

        string s = peers.Count == 0 ? "voce esta sozinho na sala — chama a galera"
                 : semRota > 0
                     ? $"{semRota} sem rota — o furo de NAT nao fechou. Provavel NAT simetrico "
                       + "(4G/CGNAT) ou firewall do Windows bloqueando o Primicord."
                 : punching == 0 ? $"{connected + 1} na call · conectado direto (P2P)"
                 : $"{connected + 1} na call · {punching} conectando...";

        if (_session.PublicEndpoint == null) s += " · sem STUN (so conecta na mesma rede)";

        // Com WebRTC a voz tem estado PROPRIO: a malha pode estar conectada (tela
        // passando) e a voz ainda negociando. Mostrar so o da malha esconderia isso.
        if (_webrtc != null)
        {
            int ok = _webrtc.ConnectedCount, total = _webrtc.LinkCount;
            s += total == 0 ? " · voz WebRTC"
               : ok == total ? $" · voz WebRTC (Opus, {ok}/{total})"
               : $" · voz WebRTC negociando ({ok}/{total})";
        }

        _roomStatus.Text = s;
        if (_roomHeader != null)
        {
            _roomHeader.RoomName = _voiceRoomName;
            _roomHeader.Participants = peers.Count + 1;
        }
        if (_roomActivity != null)
        {
            string connection = semRota > 0 ? $"{semRota} conexao sem rota"
                : punching > 0 ? $"{punching} conexao conectando"
                : _webrtc != null ? "Voz WebRTC · Opus" : "Voz direta · P2P";
            _roomActivity.UpdateSnapshot(peers.Count + 1, connection);
        }
    }

    private void ToggleMute()
    {
        if (_session == null) return;
        bool muted = !_session.Muted;
        // A malha sempre sabe: e ela que publica o estado de mudo na presenca do
        // Firestore, que e o que os outros veem no tile.
        _session.Muted = muted;
        if (_webrtc != null) _webrtc.Muted = muted;
    }

    // ─── TELA ────────────────────────────────────────────────────────────────

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

        try
        {
            _screenSender = new ScreenSender(_session, targets[pick])
            { TotalUploadBudget = Math.Clamp(_cfg.ScreenBudgetKb, 200, 6000) * 1000 };
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
            // Tela sem som e tela pela metade: o pessoal veria o jogo mudo.
            _screenAudio = _cfg.ShareAudioWithScreen;
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
    // ─── CAMERA ──────────────────────────────────────────────────────────────

    private void ToggleWebcam()
    {
        if (_session == null) { ShowBanner("Entra numa sala de voz primeiro."); return; }

        if (_camOn)
        {
            try { _cam?.Dispose(); } catch { }
            _cam = null;
            _camOn = false;
            _cams.Remove(MyCamId);
            if (_myTile != null) { _myTile.Cam = null; _myTile.Invalidate(); }
            SyncRoomButtons();
            Toast("CAMERA DESLIGADA", "");
            return;
        }

        _cam = new WebcamCapture(_cfg.CamFps);
        _cam.Failed += ShowBanner;
        _cam.FrameReady += OnMyCamFrame;
        _camOn = true;
        SyncRoomButtons();

        // Abrir camera demora (o driver negocia formato): async pra nao travar a UI.
        _ = Task.Run(async () =>
        {
            bool ok = await _cam.StartAsync(_cfg.CamDevice, _cfg.CamWidth, _cfg.CamHeight);
            if (!IsDisposed) BeginInvoke(() =>
            {
                if (!ok) { _camOn = false; try { _cam?.Dispose(); } catch { } _cam = null; }
                else Toast("CAMERA LIGADA", $"{_cam!.Width}x{_cam.Height}"
                                          + (_cam.NativeJpeg ? "" : " (convertendo)"));
                SyncRoomButtons();
            });
        });
    }

    /// <summary>Meu proprio quadro: vai pra rede e pro meu tile (thread da camera).</summary>
    private void OnMyCamFrame(byte[] jpeg, int w, int h)
    {
        _session?.SendWebcamFrame(jpeg, jpeg.Length, w, h);
        _cams.OnFrame(MyCamId, jpeg);   // previa local, pelo mesmo caminho dos outros
    }

    private void OnPeerCam(uint senderId, byte[] jpeg, int w, int h)
        => _cams.OnFrame(senderId, jpeg);

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
        if (_clips?.Active != true) return;
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
        if (_clips.Active) { _clips.Stop(); _bufferOffByUser = true; }
        else { _clips.Start(); _bufferOffByUser = false; }
        SyncRoomButtons();
    }

    private bool _bufferOffByUser;

    private void SaveClip()
    {
        if (_clips == null || !_clips.Active)
        {
            ShowBanner("Liga o GRAVAR primeiro — o clipe sai do que ficou no buffer.");
            return;
        }
        string? path = _clips.SaveClip(_cfg.ClipSeconds, _voiceRoomName);
        if (path == null) { ShowBanner("Buffer ainda vazio — espera uns segundos."); return; }

        // Abre a pasta com o arquivo ja selecionado.
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception ex) { Log.Write("abrir pasta falhou: " + ex.Message); }
    }

    // ─── MODO DJ ─────────────────────────────────────────────────────────────

    private bool _djMode;
    private bool _screenAudio;

    private void ToggleDj()
    {
        if (_session == null) { ShowBanner("Entra numa sala de voz primeiro."); return; }
        _djMode = !_djMode;
        if (!_djMode) _nowPlaying = "";
        SyncSystemAudio();
        SyncRoomButtons();
    }

    /// <summary>
    /// Liga/desliga a captura do som do sistema. Existe UMA so, compartilhada entre
    /// "tela com audio" e "modo DJ" — abrir duas capturas do mesmo dispositivo
    /// duplicaria o audio na sala.
    /// </summary>
    private void SyncSystemAudio()
    {
        bool querem = _djMode || (_screenAudio && _iAmSharing);

        if (!querem)
        {
            if (_music != null) { _music.Dispose(); _music = null; Log.Write("som do sistema: parou"); }
            return;
        }
        if (_music != null) return;   // ja rodando

        try
        {
            _music = new MusicShare(_session!);
            _music.Start();
            if (_music.EchoRisk)
                ShowBanner("Windows sem process loopback: o audio das vozes vai voltar junto (eco).");
        }
        catch (Exception ex)
        {
            Log.Write("som do sistema falhou: " + ex.Message);
            ShowBanner("Nao consegui capturar o audio do sistema: " + ex.Message);
            _music = null;
            _djMode = false;
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
        _icMic.Active = !muted;
        _icMic.Caption = "MICROFONE";
        _icMic.Subtitle = muted ? "Mutado" : "Ativo";
        _icMic.Invalidate();

        if (_icAudio != null)
        {
            _icAudio.Alert = _audioMuted;
            _icAudio.Active = !_audioMuted;
            _icAudio.Caption = "AUDIO";
            _icAudio.Subtitle = _audioMuted ? "Silenciado" : "Alto";
            _icAudio.Invalidate();
        }

        if (_icInvite != null)
        {
            _icInvite.Caption = "CONVIDAR";
            _icInvite.Subtitle = "amigos";
            _icInvite.Invalidate();
        }
        if (_icLeave != null)
        {
            _icLeave.Caption = "SAIR DA SALA";
            _icLeave.Subtitle = "voltar ao lobby";
            _icLeave.Invalidate();
        }

        if (_icChat != null)
        {
            _icChat.Active = _roomChatVisible;
            _icChat.Invalidate();
        }

        _icCam!.Active = _camOn;
        _icCam.Caption = _camOn ? "NO AR" : "CAMERA";
        _icCam.Invalidate();

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

        _icDj!.Active = _djMode;
        _icDj.Invalidate();

        if (_djLabel != null)
        {
            bool show = _music != null && _nowPlaying.Length > 0;
            _djLabel.Text = show ? "TOCANDO AGORA: " + _nowPlaying.ToUpperInvariant() : "";
            _djLabel.Visible = show;
        }

        // Selo de sessao local: avisa que a qualidade esta liberada.
        if (_lanBadge != null)
        {
            bool lan = _screenSender?.LanSession == true;
            _lanBadge.Text = lan ? "SESSAO EM LAN — QUALIDADE LIBERADA" : "";
            _lanBadge.Visible = lan;
        }
    }

    private void StopTimers()
    {
        try { _pollTimer?.Stop(); _pollTimer?.Dispose(); } catch { }
        _pollTimer = null;
        try { _voiceTimer?.Stop(); _voiceTimer?.Dispose(); } catch { }
        _voiceTimer = null;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!_view.StartsWith("room:") || WindowState == FormWindowState.Minimized) return;

        if (ClientSize.Width < 900 && _roomChatVisible)
        {
            _roomChatVisible = false;
            _chatAutoCollapsed = true;
            UpdateRightContext();
            SyncRoomButtons();
        }
        else if (ClientSize.Width >= 960 && _chatAutoCollapsed)
        {
            _roomChatVisible = true;
            _chatAutoCollapsed = false;
            UpdateRightContext();
            SyncRoomButtons();
        }
    }

    // ─── ATALHO GLOBAL DO CLIPE ──────────────────────────────────────────────

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyClipHotkey();
    }

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
    private void OnClipHotkey()
    {
        if (_clips == null || !_clips.Active)
        {
            Chime.Fail();
            Toast("SEM BUFFER", "Entra numa sala com alguem compartilhando tela.");
            return;
        }

        string? path = _clips.SaveClip(_cfg.ClipSeconds, _voiceRoomName);
        if (path == null)
        {
            Chime.Fail();
            Toast("BUFFER VAZIO", "Espera uns segundos de tela antes de clipar.");
            return;
        }
        Chime.Ok();
        Toast("CLIPE SALVO", $"ultimos {_cfg.ClipSeconds}s · {Path.GetFileName(path)}");
        Log.Write("clipe pelo atalho: " + path);
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
        try { _voice?.Dispose(); } catch { }
        try { _session?.Dispose(); } catch { }
        base.OnFormClosing(e);
    }

    private static string Sanitize(string s)
    {
        var chars = s.ToLowerInvariant().Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9').ToArray();
        string outp = new(chars);
        return outp.Length == 0 ? "primitivo" : outp[..Math.Min(12, outp.Length)];
    }
}
