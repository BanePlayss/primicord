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

    private Panel? _railList, _voiceStrip, _userPanel, _membersList, _contentHost;
    private ClassificacaoView? _tabela;
    private Label? _tabelaHead;
    private ChatView? _chatView;
    private Panel? _roomPanel;
    private FlowLayoutPanel? _tiles;
    private readonly Dictionary<uint, PeerTile> _peerTiles = new();
    private PeerTile? _myTile;
    private Label? _roomStatus;

    // estado da navegacao: "geral" | "dm:<nick>" | "room:<id>"
    private string _view = "geral";
    private List<RoomInfo> _rooms = new();
    private List<string> _members = new();
    private Dictionary<string, MemberPresence> _presence = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _openDms = new();

    private System.Windows.Forms.Timer? _pollTimer, _voiceTimer;
    private int _pollTick;
    private bool _polling;

    public MainForm()
    {
        _dir = new RoomDirectory(_fs);
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
        _cfg.Nick = user.Nick;
        _cfg.SenhaHash = user.SenhaHash;
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
        var rail = new Panel { Dock = DockStyle.Left, Width = 236, BackColor = Pv.Char2 };
        rail.Paint += (_, e) =>
        {
            using var p = new Pen(Pv.Char3, 2);
            e.Graphics.DrawLine(p, rail.Width - 1, 0, rail.Width - 1, rail.Height);
        };

        var brand = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = Pv.Char2 };
        brand.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var b = new SolidBrush(Pv.Orange))
                Pv.DrawTracked(g, "PRIMICORD", Pv.DisplaySm, b, 16, 18, 2.2f);
            using (var p = new Pen(Pv.Char3, 2)) g.DrawLine(p, 0, brand.Height - 1, brand.Width, brand.Height - 1);
        };

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
        rail.Controls.Add(brand);

        // ── MEMBROS (direita) ──
        var members = new Panel { Dock = DockStyle.Right, Width = 212, BackColor = Pv.Char2 };
        members.Paint += (_, e) =>
        {
            using var p = new Pen(Pv.Char3, 2);
            e.Graphics.DrawLine(p, 0, 0, 0, members.Height);
        };
        var mHead = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Pv.Char2 };
        mHead.Paint += (_, e) =>
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
        var campeonato = new Panel { Dock = DockStyle.Bottom, AutoSize = true, BackColor = Pv.Char2 };
        _tabelaHead = new Label
        {
            Dock = DockStyle.Top, Height = 34, BackColor = Pv.Char2, ForeColor = Pv.BoneDim,
            Font = Pv.Label, Padding = new Padding(18, 12, 8, 0), Text = "LOL",
        };
        _tabela = new ClassificacaoView { Dock = DockStyle.Top };
        campeonato.Controls.Add(_tabela);
        campeonato.Controls.Add(_tabelaHead);
        campeonato.Paint += (_, e) =>
        {
            using var p = new Pen(Pv.Char3, 2);
            e.Graphics.DrawLine(p, 12, 0, campeonato.Width - 12, 0);
        };

        // Fill primeiro, depois as bordas: o WinForms encaixa na ordem inversa.
        members.Controls.Add(_membersList);
        members.Controls.Add(campeonato);
        members.Controls.Add(mHead);

        // ── CONTEUDO ──
        _contentHost = new Panel { Dock = DockStyle.Fill, BackColor = Pv.Charcoal };

        host.Controls.Add(_contentHost);   // Fill primeiro
        host.Controls.Add(members);
        host.Controls.Add(rail);
        SetBody(host);

        _chatView = new ChatView { Dock = DockStyle.Fill };
        _chatView.Send += OnSendMessageAsync;

        SelectView("geral");
        RebuildRail();

        _pollTimer = new System.Windows.Forms.Timer { Interval = 2000 };
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

    private void UpdateVoiceStrip()
    {
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
                Suffix = room.Count > 0 ? room.Count.ToString() : "",
            };
            string id = room.Id, name = room.Name;
            it.Click += async (_, _) => await OnRoomClickedAsync(id, name);
            items.Add(it);
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
            _ = RefreshChatAsync();
        }
        _contentHost.ResumeLayout();
        RebuildRail();
    }

    private async Task OnRoomClickedAsync(string roomId, string roomName)
    {
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

            // Classificacao: a cada ~60s, nao a cada 2s como o resto. Placar de
            // futebol nao muda em dois segundos, e o doc, por menor que seja, e
            // uma leitura a mais por ciclo pra todo mundo que estiver com o app
            // aberto. O primeiro tick busca na hora pra tabela nao nascer vazia.
            // O doc do LoL tem 134KB mesmo com mascara (contra 4KB do FIFA), entao
            // este e mais espacado ainda: a cada ~2min. Placar de campeonato que
            // roda uma vez por semana nao precisa de mais que isso.
            if (_pollTick == 1 || _pollTick % 60 == 0) await RefreshCampeonatoAsync();

            try { _presence = await _chat.ReadPresenceAsync(); } catch { }

            var rooms = await _dir.ListAsync();
            bool roomsChanged = rooms.Count != _rooms.Count ||
                rooms.Zip(_rooms).Any(t => t.First.Id != t.Second.Id || t.First.Count != t.Second.Count);
            _rooms = rooms;

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

    /// <summary>Le a tabela do Primitivao e joga na coluna da direita.</summary>
    private async Task RefreshCampeonatoAsync()
    {
        if (_tabela == null || _tabela.IsDisposed) return;
        try
        {
            var tabela = await CampeonatoLol.LerAsync(_fs);
            if (_tabela.IsDisposed) return;
            _tabela.MeuNick = Nick;
            _tabela.MaxLinhas = 4;   // o TOP 4, como no rascunho
            _tabela.Definir(tabela);
            if (_tabelaHead != null && !_tabelaHead.IsDisposed)
            {
                string resumo = _tabela.Resumo;
                _tabelaHead.Text = resumo.Length > 0 ? "LOL  ·  " + resumo : "LOL";
            }
        }
        catch (Exception ex) { Log.Write("campeonato: " + ex.Message); }
    }

    private async Task RefreshChatAsync()
    {
        if (_chat == null || _chatView == null || _chatView.IsDisposed) return;
        if (_view.StartsWith("room:")) return;
        try
        {
            var msgs = _view == "geral"
                ? await _chat.ReadChannelAsync(ChatService.GeneralChannel)
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

    private ActionIcon? _icMic, _icCam, _icShare, _icRec, _icClip, _icDj, _icCinema, _icQuick, _icLeave;
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

        _icLeave = new ActionIcon(Glyphs.Exit, "SAIR") { ToolTipText = "Sair da sala de voz" };
        _icLeave.Click += (_, _) => LeaveVoice();

        _lanBadge = new Label
        {
            Font = Pv.Label, ForeColor = Pv.Green, AutoSize = true, Text = "",
            Margin = new Padding(14, 22, 0, 0), Visible = false,
        };

        foreach (var ic in new[] { _icMic, _icCam, _icShare, _icRec, _icClip, _icDj, _icCinema, _icQuick, _icLeave })
            ic!.Margin = new Padding(0, 0, 6, 0);
        bar.Controls.AddRange(new Control[]
            { _icMic, _icCam, _icShare, _icRec, _icClip, _icDj, _icCinema, _icQuick, _icLeave, _lanBadge });
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

        string peerId = Sanitize(Nick) + "-" + Random.Shared.Next(0x10000, 0xFFFFF).ToString("x5");
        _session = new RoomSession(_fs, roomId, peerId, Nick);
        _session.PeersChanged += OnPeersChanged;
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
        _voiceRoomId = "";
        _voiceRoomName = "";
        _peerTiles.Clear();
        _myTile = null;

        UpdateVoiceStrip();
        if (_view.StartsWith("room:")) SelectView("geral");
    }

    private void OnPeersChanged()
    {
        if (InvokeRequired) { BeginInvoke(OnPeersChanged); return; }
        if (_session == null || _tiles == null || _tiles.IsDisposed) return;

        var peers = _session.Peers;
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
            tile.Connected = p.Connected;
            tile.Punching = p.Locked == null;
            tile.SilentSeconds = p.SilentSeconds;
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
            _cams.Remove(sid);
            if (_focusedSharer == sid) _focusedSharer = 0;
        }
        UpdateRoomStatus();
    }

    private int _stageTick;

    private void TickVoice()
    {
        if (_voice == null || _session == null) return;

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
        _icMic.Caption = muted ? "MUDO" : "MIC";
        _icMic.Invalidate();

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
