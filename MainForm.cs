namespace Primicord;

/// <summary>
/// Janela unica: alterna entre o LOBBY (lista de salas) e a SALA (quem esta na call).
/// </summary>
/// <remarks>
/// Layout e todo por DOCK + reposicionamento no Resize, nunca por coordenada fixa
/// calculada do ClientSize no construtor — o Windows escala a janela por DPI (125%,
/// 150%...) e tamanho fixo estoura pra fora da tela.
/// </remarks>
public sealed class MainForm : Form
{
    private readonly Firestore _fs = new();
    private readonly RoomDirectory _dir;
    private readonly Config _cfg;

    /// <summary>Quem esta logado — vem do Primitivao (mesma conta do site).</summary>
    private PrimitivaoUser? _me;
    private Panel? _header;
    private string Nick => _me?.Nick ?? _cfg.Nick;

    private RoomSession? _session;
    private VoiceEngine? _voice;
    private string _roomName = "";

    // shell
    private readonly Panel _body = new() { Dock = DockStyle.Fill, BackColor = Pv.Charcoal };
    private readonly Label _banner = new()
    {
        Dock = DockStyle.Top, Height = 0, BackColor = Pv.Red, ForeColor = Pv.Bone,
        TextAlign = ContentAlignment.MiddleCenter, Font = Pv.Body, Visible = false,
    };

    // lobby
    private Panel? _roomList;
    private PrimInput? _newRoomInput;
    private System.Windows.Forms.Timer? _lobbyTimer;

    // sala
    private FlowLayoutPanel? _tiles;
    private readonly Dictionary<uint, PeerTile> _peerTiles = new();
    private PeerTile? _myTile;
    private PrimButton? _muteBtn;
    private Label? _roomStatus;
    private System.Windows.Forms.Timer? _roomTimer;

    public MainForm()
    {
        _dir = new RoomDirectory(_fs);
        _cfg = Config.Load();

        Text = "PRIMICORD";
        // O icone vem embutido no proprio exe (ApplicationIcon) — funciona tambem
        // no publish single-file, onde nao existe .ico solto ao lado do binario.
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe != null) Icon = Icon.ExtractAssociatedIcon(exe);
        }
        catch (Exception ex) { Log.Write("icone da janela: " + ex.Message); }

        ClientSize = new Size(960, 660);
        MinimumSize = new Size(720, 540);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Pv.Charcoal;
        ForeColor = Pv.Bone;
        Font = Pv.Body;
        DoubleBuffered = true;

        _header = BuildHeader();
        Controls.Add(_body);      // Fill entra primeiro: fica com o espaco restante
        Controls.Add(_banner);
        Controls.Add(_header);

        ShowLogin();
        // Ja logou antes? Re-valida em silencio contra o Primitivao.
        if (!string.IsNullOrWhiteSpace(_cfg.Nick) && !string.IsNullOrWhiteSpace(_cfg.SenhaHash))
            _ = TryAutoLoginAsync();
    }

    // ─── SHELL ───────────────────────────────────────────────────────────────

    private Panel BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = Pv.Charcoal };
        header.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var b = new SolidBrush(Pv.Orange))
                Pv.DrawTracked(g, "PRIMICORD", Pv.Display, b, 18, 14, 2.5f);
            using (var p = new Pen(Pv.Orange, 3))
                g.DrawLine(p, 0, header.Height - 2, header.Width, header.Height - 2);

            if (_me == null) return;

            // Canto direito: foto do site + nick + saldo de PC (dados do Primitivao).
            int right = header.Width - 18;
            const int av = 36;
            var ac = new Rectangle(right - av, (header.Height - av) / 2 - 1, av, av);
            var photo = Primitivao.AvatarFor(_me.Nick);
            if (photo != null)
            {
                using var clip = new System.Drawing.Drawing2D.GraphicsPath();
                clip.AddEllipse(ac);
                var saved = g.Save();
                g.SetClip(clip);
                int side = Math.Min(photo.Width, photo.Height);
                g.DrawImage(photo, ac,
                    new Rectangle((photo.Width - side) / 2, (photo.Height - side) / 2, side, side),
                    GraphicsUnit.Pixel);
                g.Restore(saved);
                using var pen = new Pen(Pv.Orange, 2);
                g.DrawEllipse(pen, ac);
            }
            else
            {
                using var b = new SolidBrush(Pv.Orange);
                g.FillEllipse(b, ac);
                using var f = new Font("Bahnschrift", 15f, FontStyle.Bold);
                using var tb = new SolidBrush(Pv.Charcoal);
                string ini = _me.Nick[..1].ToUpperInvariant();
                var sz = g.MeasureString(ini, f);
                g.DrawString(ini, f, tb, ac.X + (av - sz.Width) / 2, ac.Y + (av - sz.Height) / 2);
            }

            float textRight = ac.Left - 12;
            string who = _me.Nick.ToUpperInvariant();
            if (_me.Badge.Length > 0) who += " · " + _me.Badge;
            using (var b = new SolidBrush(Pv.Bone))
            {
                float w = Pv.TrackedWidth(g, who, Pv.Label, 1.8f);
                Pv.DrawTracked(g, who, Pv.Label, b, textRight - w, 16, 1.8f);
            }

            string coins = _me.PcShort + " PC";
            if (_me.TeamName.Length > 0) coins = _me.TeamName.ToUpperInvariant() + " · " + coins;
            using (var b = new SolidBrush(Pv.Orange))
            {
                float w = Pv.TrackedWidth(g, coins, Pv.Label, 1.2f);
                Pv.DrawTracked(g, coins, Pv.Label, b, textRight - w, 34, 1.2f);
            }
        };
        header.Resize += (_, _) => header.Invalidate();
        return header;
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

    // ─── LOGIN (conta do Primitivao) ─────────────────────────────────────────

    private PrimInput? _loginNick;
    private PrimInput? _loginPass;
    private Label? _loginErr;
    private PrimButton? _loginBtn;

    private void ShowLogin(string? presetNick = null)
    {
        _me = null;
        _header?.Invalidate();

        var host = new Panel { BackColor = Pv.Charcoal };
        var card = new Panel { Size = new Size(410, 300), BackColor = Pv.Char2 };
        card.Paint += (_, e) =>
        {
            using var p = new Pen(Pv.Char3, 2);
            e.Graphics.DrawRectangle(p, 0, 0, card.Width - 1, card.Height - 1);
        };

        var title = new Label
        {
            Text = "ENTRAR", Font = Pv.DisplaySm, ForeColor = Pv.Bone,
            Location = new Point(24, 20), AutoSize = true,
        };
        var hint = new Label
        {
            Text = "Mesma conta do site do Primitivao.", Font = Pv.Body, ForeColor = Pv.BoneDim,
            Location = new Point(24, 48), AutoSize = true,
        };

        var lblNick = SectionLabel("NICK");
        lblNick.Location = new Point(24, 82);
        _loginNick = new PrimInput("seu nick")
        {
            Location = new Point(24, 100), Width = 362,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        _loginNick.Value = presetNick ?? _cfg.Nick;

        var lblPass = SectionLabel("SENHA");
        lblPass.Location = new Point(24, 148);
        _loginPass = new PrimInput("sua senha")
        {
            Location = new Point(24, 166), Width = 362,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        _loginPass.Box.UseSystemPasswordChar = true;

        _loginErr = new Label
        {
            Text = "", Font = Pv.Body, ForeColor = Pv.Red, Location = new Point(24, 210),
            Size = new Size(362, 34), AutoSize = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        _loginBtn = new PrimButton("ENTRAR")
        {
            Location = new Point(24, 244), Size = new Size(362, 40),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        _loginBtn.Click += async (_, _) => await DoLoginAsync();
        _loginNick.Box.KeyDown += async (_, e) =>
        { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _loginPass.Box.Focus(); } };
        _loginPass.Box.KeyDown += async (_, e) =>
        { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await DoLoginAsync(); } };

        card.Controls.AddRange(new Control[]
            { title, hint, lblNick, _loginNick, lblPass, _loginPass, _loginErr, _loginBtn });
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
        if (_loginNick == null || _loginPass == null || _loginBtn == null) return;
        SetLoginBusy(true, "");
        var res = await Primitivao.AuthenticateAsync(_fs, _loginNick.Value, _loginPass.Value);
        if (!res.Ok)
        {
            SetLoginBusy(false, res.Error ?? "Nao consegui entrar");
            return;
        }
        OnLoggedIn(res.User!);
    }

    /// <summary>Boot: revalida a sessao salva sem incomodar o usuario.</summary>
    private async Task TryAutoLoginAsync()
    {
        SetLoginBusy(true, "");
        var res = await Primitivao.AuthenticateWithHashAsync(_fs, _cfg.Nick, _cfg.SenhaHash);
        if (res.Ok) { OnLoggedIn(res.User!); return; }

        // Senha mudou no site, ou conta sumiu: cai no login normal.
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
        _header?.Invalidate();
        ShowLobby();
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
        _cfg.SenhaHash = "";
        _cfg.Save();
        ShowLogin(_cfg.Nick);
    }

    // ─── LOBBY ───────────────────────────────────────────────────────────────

    private void ShowLobby()
    {
        StopRoomTimers();

        var host = new Panel { BackColor = Pv.Charcoal };

        // Topo: criar sala.
        var top = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Pv.Charcoal };
        var lblNew = SectionLabel("ABRIR SALA NOVA");
        lblNew.Location = new Point(24, 16);
        _newRoomInput = new PrimInput("nome da sala (ex: RANQUEADA, RESENHA...)")
        { Location = new Point(24, 40) };
        var createBtn = new PrimButton("CRIAR") { Size = new Size(130, 38) };
        createBtn.Click += async (_, _) => await CreateRoomAsync();
        _newRoomInput.Box.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await CreateRoomAsync(); }
        };
        void LayoutTop()
        {
            int right = top.ClientSize.Width - 24;
            createBtn.Location = new Point(right - createBtn.Width, 40);
            _newRoomInput.Width = Math.Max(160, createBtn.Left - 12 - 24);
        }
        top.Resize += (_, _) => LayoutTop();
        top.Controls.AddRange(new Control[] { lblNew, _newRoomInput, createBtn });

        // Rodape: dispositivos de audio.
        var devices = BuildDevicePanel();

        // Rotulo da lista + trocar de conta.
        var lblRooms = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Pv.Charcoal };
        var lr = SectionLabel("SALAS");
        lr.Location = new Point(24, 12);
        var logoutBtn = new PrimButton("TROCAR CONTA", PrimButton.Style.Ghost) { Size = new Size(140, 28) };
        logoutBtn.Click += (_, _) => Logout();
        lblRooms.Resize += (_, _) =>
            logoutBtn.Location = new Point(lblRooms.ClientSize.Width - logoutBtn.Width - 24, 3);
        lblRooms.Controls.AddRange(new Control[] { lr, logoutBtn });
        logoutBtn.Location = new Point(Math.Max(160, ClientSize.Width - logoutBtn.Width - 24), 3);

        // Lista (preenche o resto).
        _roomList = new Panel
        {
            Dock = DockStyle.Fill, AutoScroll = true, BackColor = Pv.Charcoal,
            Padding = new Padding(24, 0, 24, 12),
        };

        host.Controls.Add(_roomList);   // Fill primeiro
        host.Controls.Add(lblRooms);
        host.Controls.Add(top);
        host.Controls.Add(devices);
        SetBody(host);
        LayoutTop();

        _lobbyTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _lobbyTimer.Tick += async (_, _) => await RefreshRoomsAsync();
        _lobbyTimer.Start();
        _ = RefreshRoomsAsync();
    }

    private Panel BuildDevicePanel()
    {
        var p = new Panel { Dock = DockStyle.Bottom, Height = 84, BackColor = Pv.Charcoal };
        p.Paint += (_, e) =>
        {
            using var pen = new Pen(Pv.Char3, 2);
            e.Graphics.DrawLine(pen, 24, 1, p.Width - 24, 1);
        };

        var micLbl = SectionLabel("MICROFONE");
        micLbl.Location = new Point(24, 14);
        var micCombo = new ComboBox
        {
            Location = new Point(24, 36), DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat, BackColor = Pv.Char2, ForeColor = Pv.Bone, Font = Pv.Body,
        };
        foreach (var d in VoiceEngine.ListInputs()) micCombo.Items.Add(d);
        if (micCombo.Items.Count > 0)
            micCombo.SelectedIndex = Math.Clamp(_cfg.MicDevice, 0, micCombo.Items.Count - 1);
        micCombo.SelectedIndexChanged += (_, _) =>
        {
            if (micCombo.SelectedItem is InputDeviceItem it) { _cfg.MicDevice = it.DeviceNumber; _cfg.Save(); }
        };

        var outLbl = SectionLabel("SAIDA (USE FONE — SEM CANCELAMENTO DE ECO)");
        var outCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat, BackColor = Pv.Char2, ForeColor = Pv.Bone, Font = Pv.Body,
        };
        outCombo.Items.Add(new OutputDeviceItem("", "Padrao do Windows"));
        foreach (var d in VoiceEngine.ListOutputs()) outCombo.Items.Add(d);
        outCombo.SelectedIndex = 0;
        for (int i = 0; i < outCombo.Items.Count; i++)
            if (outCombo.Items[i] is OutputDeviceItem o && o.Id == _cfg.OutputDeviceId) outCombo.SelectedIndex = i;
        outCombo.SelectedIndexChanged += (_, _) =>
        {
            if (outCombo.SelectedItem is OutputDeviceItem it) { _cfg.OutputDeviceId = it.Id; _cfg.Save(); }
        };

        void LayoutDev()
        {
            int usable = p.ClientSize.Width - 48;
            int col = Math.Max(140, (usable - 20) / 2);
            micCombo.Width = col;
            outLbl.Location = new Point(24 + col + 20, 14);
            outCombo.Location = new Point(24 + col + 20, 36);
            outCombo.Width = col;
        }
        p.Resize += (_, _) => LayoutDev();
        p.Controls.AddRange(new Control[] { micLbl, micCombo, outLbl, outCombo });
        LayoutDev();
        return p;
    }

    private async Task CreateRoomAsync()
    {
        string name = _newRoomInput?.Value.Trim() ?? "";
        if (string.IsNullOrEmpty(name)) return;
        try
        {
            var room = await _dir.CreateAsync(name, _cfg.Nick);
            if (_newRoomInput != null) _newRoomInput.Value = "";
            await JoinRoomAsync(room.Id, room.Name);
        }
        catch (FirestoreException ex) when (ex.IsPermissionDenied)
        {
            ShowBanner("O Firestore recusou a escrita — falta publicar as rules do pc_rooms no Firebase Console.");
        }
        catch (Exception ex)
        {
            Log.Write("criar sala falhou: " + ex.Message);
            ShowBanner("Nao consegui criar a sala: " + ex.Message);
        }
    }

    private async Task RefreshRoomsAsync()
    {
        if (_roomList == null || _roomList.IsDisposed) return;
        List<RoomInfo> rooms;
        try { rooms = await _dir.ListAsync(); }
        catch (FirestoreException ex) when (ex.IsPermissionDenied)
        {
            ShowBanner("O Firestore recusou a leitura — falta publicar as rules do pc_rooms no Firebase Console.");
            _lobbyTimer?.Stop();
            return;
        }
        catch (Exception ex) { Log.Write("listar salas falhou: " + ex.Message); return; }

        if (_roomList.IsDisposed) return;
        _roomList.SuspendLayout();
        foreach (Control c in _roomList.Controls.Cast<Control>().ToList()) c.Dispose();
        _roomList.Controls.Clear();

        if (rooms.Count == 0)
        {
            _roomList.Controls.Add(new Label
            {
                Text = "Nenhuma sala ainda. Abre a primeira ai em cima.",
                ForeColor = Pv.BoneDim, Font = Pv.Body, AutoSize = true, Location = new Point(4, 10),
            });
        }
        else
        {
            // Dock=Top empilha na ordem INVERSA de adicao — por isso o Reverse().
            foreach (var room in Enumerable.Reverse(rooms))
                _roomList.Controls.Add(BuildRoomRow(room));
        }
        _roomList.ResumeLayout();
    }

    private Panel BuildRoomRow(RoomInfo room)
    {
        // Height inclui 10px de respiro embaixo; a moldura e desenhada so nos 68 de cima.
        var row = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Pv.Charcoal };
        const int CardH = 68;

        row.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var card = new Rectangle(0, 0, row.Width - 1, CardH);
            using (var b = new SolidBrush(Pv.Char2)) g.FillRectangle(b, card);
            using (var p = new Pen(Pv.Char3, 2)) g.DrawRectangle(p, card);
            using (var b = new SolidBrush(Pv.Bone))
                Pv.DrawTracked(g, room.Name, Pv.DisplaySm, b, 16, 12, 1.6f);

            string meta = room.Count > 0
                ? room.Count + " na sala: " + string.Join(", ", room.Occupants)
                : "vazia · criada por " + (room.CreatedBy.Length > 0 ? room.CreatedBy : "?");
            using (var b = new SolidBrush(room.Count > 0 ? Pv.Green : Pv.BoneDim))
                g.DrawString(meta, Pv.Body, b, 16, 38);
        };

        var enter = new PrimButton("ENTRAR") { Size = new Size(112, 34) };
        enter.Click += async (_, _) => await JoinRoomAsync(room.Id, room.Name);
        row.Controls.Add(enter);

        PrimButton? del = null;
        if (room.Count == 0)
        {
            del = new PrimButton("X", PrimButton.Style.Ghost) { Size = new Size(38, 34) };
            del.Click += async (_, _) =>
            {
                if (MessageBox.Show($"Excluir a sala \"{room.Name}\"?", "PRIMICORD",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                try { await _dir.DeleteAsync(room.Id); await RefreshRoomsAsync(); }
                catch (Exception ex) { Log.Write("excluir falhou: " + ex.Message); }
            };
            row.Controls.Add(del);
        }

        void LayoutRow()
        {
            int y = (CardH - enter.Height) / 2;
            enter.Location = new Point(row.ClientSize.Width - enter.Width - 16, y);
            if (del != null) del.Location = new Point(enter.Left - del.Width - 8, y);
        }
        row.Resize += (_, _) => LayoutRow();
        LayoutRow();
        return row;
    }

    // ─── SALA ────────────────────────────────────────────────────────────────

    private async Task JoinRoomAsync(string roomId, string roomName)
    {
        _lobbyTimer?.Stop();
        _roomName = roomName;

        string peerId = Sanitize(_cfg.Nick) + "-" + Random.Shared.Next(0x10000, 0xFFFFF).ToString("x5");
        _session = new RoomSession(_fs, roomId, peerId, _cfg.Nick);
        _session.PeersChanged += OnPeersChanged;
        _session.Failed += ShowBanner;

        _voice = new VoiceEngine();
        _voice.Failed += ShowBanner;
        _voice.AttachSession(_session);

        BuildRoomUi();

        try
        {
            _voice.Start(_cfg.MicDevice, string.IsNullOrEmpty(_cfg.OutputDeviceId) ? null : _cfg.OutputDeviceId);
            await _session.StartAsync();
            UpdateRoomStatus();
        }
        catch (FirestoreException ex) when (ex.IsPermissionDenied)
        {
            ShowBanner("O Firestore recusou a escrita — falta publicar as rules do pc_rooms no Firebase Console.");
        }
        catch (Exception ex)
        {
            Log.Write("entrar na sala falhou: " + ex.Message);
            ShowBanner("Nao consegui entrar: " + ex.Message);
        }
    }

    private void BuildRoomUi()
    {
        var host = new Panel { BackColor = Pv.Charcoal };

        // Barra de controles (rodape).
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 76, BackColor = Pv.Charcoal };
        bar.Paint += (_, e) =>
        {
            using var p = new Pen(Pv.Orange, 3);
            e.Graphics.DrawLine(p, 0, 1, bar.Width, 1);
        };

        _muteBtn = new PrimButton("MUTAR") { Size = new Size(150, 44) };
        _muteBtn.Click += (_, _) => ToggleMute();
        var leaveBtn = new PrimButton("SAIR", PrimButton.Style.Danger) { Size = new Size(130, 44) };
        leaveBtn.Click += (_, _) => LeaveRoom();

        void LayoutBar()
        {
            int total = _muteBtn.Width + 12 + leaveBtn.Width;
            int x = Math.Max(8, (bar.ClientSize.Width - total) / 2);
            int y = (bar.ClientSize.Height - _muteBtn.Height) / 2;
            _muteBtn.Location = new Point(x, y);
            leaveBtn.Location = new Point(x + _muteBtn.Width + 12, y);
        }
        bar.Resize += (_, _) => LayoutBar();
        bar.Controls.AddRange(new Control[] { _muteBtn, leaveBtn });

        // Cabecalho da sala.
        var head = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Pv.Charcoal };
        var title = new Label
        {
            Text = _roomName, Font = Pv.DisplaySm, ForeColor = Pv.Bone,
            Location = new Point(24, 14), AutoSize = true,
        };
        _roomStatus = new Label
        {
            Text = "conectando...", Font = Pv.Body, ForeColor = Pv.BoneDim,
            Location = new Point(24, 40), AutoSize = true,
        };
        head.Controls.AddRange(new Control[] { title, _roomStatus });

        // Tiles (preenchem o resto e quebram linha sozinhos).
        _tiles = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true,
            BackColor = Pv.Charcoal, Padding = new Padding(18, 4, 18, 8),
        };
        _myTile = new PeerTile { Nick = _cfg.Nick, IsMe = true, Connected = true, Margin = new Padding(6) };
        _tiles.Controls.Add(_myTile);

        host.Controls.Add(_tiles);   // Fill primeiro
        host.Controls.Add(head);
        host.Controls.Add(bar);
        SetBody(host);
        LayoutBar();

        _roomTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _roomTimer.Tick += (_, _) => TickRoom();
        _roomTimer.Start();
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
            tile.Invalidate();
        }

        foreach (uint sid in _peerTiles.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            var tile = _peerTiles[sid];
            _peerTiles.Remove(sid);
            _tiles.Controls.Remove(tile);
            tile.Dispose();
            _voice?.RemovePeer(sid);   // tira a voz morta do mixer
        }

        UpdateRoomStatus();
    }

    /// <summary>Roda a 10fps: so atualiza niveis de voz (o resto vem por evento).</summary>
    private void TickRoom()
    {
        if (_voice == null || _session == null) return;

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

        string s;
        if (peers.Count == 0) s = "voce esta sozinho na sala — chama a galera";
        else if (punching == 0) s = $"{connected + 1} na call · conectado direto (P2P)";
        else s = $"{connected + 1} na call · {punching} conectando...";

        if (_session.PublicEndpoint == null)
            s += " · sem STUN (so conecta na mesma rede)";

        _roomStatus.Text = s;
    }

    private void ToggleMute()
    {
        if (_session == null || _muteBtn == null) return;
        _session.Muted = !_session.Muted;
        _muteBtn.Text = _session.Muted ? "FALAR" : "MUTAR";
        _muteBtn.Kind = _session.Muted ? PrimButton.Style.Danger : PrimButton.Style.Solid;
        _muteBtn.Invalidate();
    }

    private void LeaveRoom()
    {
        StopRoomTimers();
        try { _voice?.Dispose(); } catch { }
        try { _session?.Dispose(); } catch { }
        _voice = null;
        _session = null;
        _peerTiles.Clear();
        _myTile = null;
        ShowLobby();
    }

    private void StopRoomTimers()
    {
        try { _roomTimer?.Stop(); _roomTimer?.Dispose(); } catch { }
        _roomTimer = null;
        try { _lobbyTimer?.Stop(); _lobbyTimer?.Dispose(); } catch { }
        _lobbyTimer = null;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        StopRoomTimers();
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
