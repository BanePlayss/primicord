using System.Drawing.Drawing2D;

namespace Primicord;

/// <summary>
/// Configuracoes (a engrenagem): entrada e saida de som, com teste de microfone.
/// </summary>
public sealed class SettingsDialog : Form
{
    private readonly Config _cfg;
    private readonly ComboBox _mic = new();
    private readonly ComboBox _out = new();
    private readonly LevelBar _level = new();
    private VoiceEngine.MicMonitor? _monitor;

    private HotkeyBox _hotkeyBox = null!;
    private readonly PrimSlider _secs = new();
    private readonly Label _secsLabel = new();
    private readonly PrimCheck _autoBuf = new("Gravar sozinho enquanto alguem compartilha tela");

    private readonly ComboBox _bw = new();
    private readonly ComboBox _fps = new();
    private readonly ComboBox _width = new();
    private readonly PrimCheck _tailnet = new("Exigir Tailscale para entrar em salas (recomendado)");

    /// <summary>Degraus de banda pro compartilhamento (KB/s, rotulo).</summary>
    /// <summary>
    /// Os rotulos mostram o que CADA degrau entregou de verdade numa medicao com a
    /// tela em movimento (video rodando) — assim da pra escolher sabendo o resultado,
    /// em vez de adivinhar pelo nome.
    /// </summary>
    private static readonly (int Kb, string Nome)[] BandwidthChoices =
    {
        (450,  "Econômico — 3,5 Mbps  ·  800x450 até 30 FPS"),
        (900,  "Equilibrado — 7 Mbps  ·  1280x720 até 30 FPS"),
        (2000, "Alta — 16 Mbps  ·  1920x1080 até 60 FPS"),
        (4000, "Máxima — 32 Mbps  ·  1080p/60 adaptativo + recuperação de pacotes"),
    };

    private static int NearestBandwidthIndex(int kb)
    {
        int best = 1, dist = int.MaxValue;
        for (int i = 0; i < BandwidthChoices.Length; i++)
        {
            int d = Math.Abs(BandwidthChoices[i].Kb - kb);
            if (d < dist) { dist = d; best = i; }
        }
        return best;
    }

    private readonly PrimCheck _siteTheme = new("Usar a cor do meu tema do Primitivao");
    private readonly PrimSlider _music = new();
    private readonly Label _musicLabel = new();
    private readonly PrimCheck _tray = new("Fechar minimiza pra bandeja (continua na call)");
    private readonly PrimCheck _joinSound = new("Bipe quando alguem entra ou sai da sala");
    private PrimButton _updateBtn = null!;
    private readonly Label _updateStatus = new();
    private UpdateInfoView? _pendingUpdate;
    private bool _updateBusy;

    private void UpdateMusicLabel()
        => _musicLabel.Text = _music.Value == 0
            ? "áudio da transmissão desligado"
            : $"áudio da transmissão em {_music.Value}% (a voz não muda)";

    private void UpdateSecsLabel()
        => _secsLabel.Text = $"o clipe salva os ultimos {_secs.Value} segundos";

    /// <summary>Disparado ao salvar, pro app aplicar sem precisar reiniciar.</summary>
    public event Action? Applied;

    public SettingsDialog(Config cfg)
    {
        _cfg = cfg;

        Text = "CONFIGURACOES";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        // Rolagem: a lista cresceu e nem toda tela cabe 1000px de altura.
        ClientSize = new Size(478, Math.Min(1040, Screen.PrimaryScreen!.WorkingArea.Height - 80));
        AutoScroll = true;
        BackColor = Pv.Char2;
        ForeColor = Pv.Bone;
        Font = Pv.Body;
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe != null) Icon = Icon.ExtractAssociatedIcon(exe);
        }
        catch { }

        var title = new Label
        {
            Text = "SOM", Font = Pv.DisplaySm, ForeColor = Pv.Bone,
            Location = new Point(24, 20), AutoSize = true,
        };

        var lblMic = Section("ENTRADA (MICROFONE)", new Point(24, 60));
        StyleCombo(_mic, new Point(24, 80));
        foreach (var d in VoiceEngine.ListInputs()) _mic.Items.Add(d);
        if (_mic.Items.Count > 0)
            _mic.SelectedIndex = Math.Clamp(IndexOfMic(cfg.MicDevice), 0, _mic.Items.Count - 1);
        _mic.SelectedIndexChanged += (_, _) => RestartMonitor();

        var lblTest = Section("TESTE — FALA ALGUMA COISA", new Point(24, 124));
        _level.Location = new Point(24, 144);
        _level.Size = new Size(412, 14);

        var lblOut = Section("SAIDA", new Point(24, 180));
        StyleCombo(_out, new Point(24, 200));
        _out.Items.Add(new OutputDeviceItem("", "Padrao do Windows"));
        foreach (var d in VoiceEngine.ListOutputs()) _out.Items.Add(d);
        _out.SelectedIndex = 0;
        for (int i = 0; i < _out.Items.Count; i++)
            if (_out.Items[i] is OutputDeviceItem o && o.Id == cfg.OutputDeviceId) _out.SelectedIndex = i;

        var warn = new Label
        {
            Text = "Usa fone. Sem cancelamento de eco, caixa de som devolve a voz dos outros.",
            Font = Pv.Body, ForeColor = Pv.BoneDim, Location = new Point(24, 238),
            Size = new Size(412, 34),
        };

        // ── CLIPES ──
        var titleClip = new Label
        {
            Text = "CLIPES", Font = Pv.DisplaySm, ForeColor = Pv.Bone,
            Location = new Point(24, 292), AutoSize = true,
        };

        var lblKey = Section("TECLA DO CLIPE — CLICA E APERTA A COMBINACAO", new Point(24, 330));
        _hotkeyBox = new HotkeyBox { Location = new Point(24, 350), Size = new Size(412, 38) };
        var (m0, k0) = HotkeyBinding.Parse(cfg.ClipHotkey, HotkeyBinding.Mods.None, Keys.F9);
        _hotkeyBox.SetCombo(m0, k0);

        var lblSecs = Section("QUANTOS SEGUNDOS PRA TRAS", new Point(24, 400));
        _secs.Location = new Point(24, 418);
        _secs.Size = new Size(412, 30);
        _secs.Minimum = 5; _secs.Maximum = 60;
        _secs.Value = Math.Clamp(cfg.ClipSeconds, 5, 60);
        _secsLabel.Location = new Point(24, 450);
        _secsLabel.AutoSize = true;
        _secsLabel.Font = Pv.Body;
        _secsLabel.ForeColor = Pv.BoneDim;
        _secs.ValueChanged += (_, _) => UpdateSecsLabel();
        UpdateSecsLabel();

        _autoBuf.Location = new Point(24, 474);
        _autoBuf.Size = new Size(412, 26);
        _autoBuf.Checked = cfg.AutoBuffer;

        // ── TELA ──
        var titleScr = new Label
        {
            Text = "COMPARTILHAR TELA", Font = Pv.DisplaySm, ForeColor = Pv.Bone,
            Location = new Point(24, 516), AutoSize = true,
        };
        var lblBw = Section("QUALIDADE — QUANTO DA SUA INTERNET PODE USAR", new Point(24, 554));
        StyleCombo(_bw, new Point(24, 574));
        foreach (var (kb, nome) in BandwidthChoices) _bw.Items.Add(nome);
        _bw.SelectedIndex = NearestBandwidthIndex(cfg.ScreenBudgetKb);

        var bwHint = new Label
        {
            Text = "A captura usa a GPU; Máxima prioriza 1080p/60 e recupera perdas de rede.\n"
                 + "O orçamento é dividido pelos espectadores; se a voz picotar, desça um degrau.",
            Font = Pv.Body, ForeColor = Pv.BoneDim, Location = new Point(24, 612),
            Size = new Size(412, 40),
        };

        var lblFps = Section("TAXA DE QUADROS", new Point(24, 662));
        StyleCombo(_fps, new Point(24, 682));
        _fps.Items.AddRange(new object[] { "60 FPS — jogos e movimento", "30 FPS — equilibrado", "15 FPS — economia" });
        _fps.SelectedIndex = cfg.ScreenFps switch { 15 => 2, 30 => 1, _ => 0 };

        var lblWidth = Section("LARGURA MÁXIMA", new Point(24, 720));
        StyleCombo(_width, new Point(24, 740));
        _width.Items.AddRange(new object[] { "1920 px — recomendado", "2560 px — ultrawide", "1280 px — economia" });
        _width.SelectedIndex = cfg.ScreenMaxWidth switch { 1280 => 2, 2560 => 1, _ => 0 };

        _tailnet.Location = new Point(24, 786);
        _tailnet.Size = new Size(412, 26);
        _tailnet.Checked = cfg.TailscaleOnly;

        var netHint = new Label
        {
            Text = "O app usa o Tailscale como rede privada. Se ele estiver desconectado,\n"
                 + "a entrada falha com uma mensagem clara em vez de cair para a internet.",
            Font = Pv.Body, ForeColor = Pv.BoneDim, Location = new Point(48, 816),
            Size = new Size(400, 42),
        };

        // ── DO PRIMITIVAO / APP ──
        var titleApp = new Label
        {
            Text = "PRIMITIVAO E APP", Font = Pv.DisplaySm, ForeColor = Pv.Bone,
            Location = new Point(24, 874), AutoSize = true,
        };

        _siteTheme.Location = new Point(24, 914);
        _siteTheme.Size = new Size(412, 26);
        _siteTheme.Checked = cfg.UseSiteTheme;

        var themeHint = new Label
        {
            Text = "A cor vem do tema escolhido no site. Trocar de tema é lá:\n"
                 + "o Primicord so le, nunca escreve no doc de apostas.",
            Font = Pv.Body, ForeColor = Pv.BoneDim, Location = new Point(48, 942),
            Size = new Size(400, 44),
        };

        var lblMusic = Section("VOLUME DO ÁUDIO DA TRANSMISSÃO", new Point(24, 990));
        _music.Location = new Point(24, 1008);
        _music.Size = new Size(412, 30);
        _music.Minimum = 0; _music.Maximum = 200;
        _music.Value = Math.Clamp(cfg.MusicVolume, 0, 200);
        _musicLabel.Location = new Point(24, 1040);
        _musicLabel.AutoSize = true;
        _musicLabel.Font = Pv.Body;
        _musicLabel.ForeColor = Pv.BoneDim;
        _music.ValueChanged += (_, _) => UpdateMusicLabel();
        UpdateMusicLabel();

        _tray.Location = new Point(24, 1070);
        _tray.Size = new Size(412, 26);
        _tray.Checked = cfg.TrayOnClose;

        _joinSound.Location = new Point(24, 1100);
        _joinSound.Size = new Size(412, 26);
        _joinSound.Checked = cfg.JoinLeaveSound;

        var titleUpdate = new Label
        {
            Text = "ATUALIZAÇÃO", Font = Pv.DisplaySm, ForeColor = Pv.Bone,
            Location = new Point(24, 1140), AutoSize = true,
        };
        var version = new Label
        {
            Text = $"você está na versão {Updater.CurrentVersion}",
            Font = Pv.Body, ForeColor = Pv.BoneDim, Location = new Point(24, 1178), AutoSize = true,
        };
        _updateBtn = new PrimButton("PROCURAR ATUALIZAÇÃO")
        { Location = new Point(24, 1204), Size = new Size(270, 38) };
        _updateBtn.AccessibleName = "Procurar atualização do Primicord";
        _updateBtn.Click += async (_, _) => await CheckOrInstallAsync();
        _updateStatus.Location = new Point(24, 1250);
        _updateStatus.Size = new Size(412, 52);
        _updateStatus.Font = Pv.Body;
        _updateStatus.ForeColor = Pv.BoneDim;
        _updateStatus.Text = Updater.CanSelfUpdate
            ? $"canal público · github.com/{Updater.DefaultRepo}"
            : "instale pelo Setup para ativar atualizações automáticas";

        var save = new PrimButton("SALVAR") { Location = new Point(24, 1320), Size = new Size(200, 40) };
        save.Click += (_, _) => Apply();
        var cancel = new PrimButton("CANCELAR", PrimButton.Style.Ghost)
        { Location = new Point(236, 1320), Size = new Size(200, 40) };
        cancel.Click += (_, _) => Close();

        Controls.AddRange(new Control[]
            { title, lblMic, _mic, lblTest, _level, lblOut, _out, warn,
              titleClip, lblKey, _hotkeyBox, lblSecs, _secs, _secsLabel, _autoBuf,
              titleScr, lblBw, _bw, bwHint, lblFps, _fps, lblWidth, _width, _tailnet, netHint,
              titleApp, _siteTheme, themeHint, lblMusic, _music, _musicLabel, _tray, _joinSound,
              titleUpdate, version, _updateBtn, _updateStatus,
              save, cancel });

        Shown += (_, _) => RestartMonitor();
        FormClosed += (_, _) => { _monitor?.Dispose(); _monitor = null; };
    }

    private async Task CheckOrInstallAsync()
    {
        if (_updateBusy) return;
        _updateBusy = true;
        _updateBtn.Enabled = false;
        try
        {
            if (_pendingUpdate == null)
            {
                _updateStatus.Text = "procurando no canal público...";
                var found = await Updater.CheckAsync().ConfigureAwait(true);
                if (found == null)
                {
                    _updateStatus.Text = $"você já está na versão mais recente ({Updater.CurrentVersion}).";
                    return;
                }
                _pendingUpdate = found;
                string notes = found.Notes.Replace("\r", "").Replace("\n", " ");
                if (notes.Length > 120) notes = notes[..120] + "...";
                _updateStatus.Text = $"versão {found.Version} disponível · "
                    + (found.IsDelta ? $"download {found.SizeLabel}" : $"pacote completo {found.SizeLabel}")
                    + (notes.Length > 0 ? "\n" + notes : "");
                _updateBtn.Text = "INSTALAR " + found.Version;
                return;
            }

            _updateStatus.Text = "baixando atualização... 0%";
            var progress = new Progress<int>(p => _updateStatus.Text = $"baixando atualização... {p}%");
            await Updater.DownloadAndApplyAsync(progress).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Write("update falhou: " + ex);
            _updateStatus.Text = "não consegui atualizar: " + ex.Message;
            _pendingUpdate = null;
            _updateBtn.Text = "PROCURAR ATUALIZAÇÃO";
        }
        finally
        {
            _updateBusy = false;
            if (!IsDisposed) _updateBtn.Enabled = true;
        }
    }

    private int IndexOfMic(int deviceNumber)
    {
        for (int i = 0; i < _mic.Items.Count; i++)
            if (_mic.Items[i] is InputDeviceItem it && it.DeviceNumber == deviceNumber) return i;
        return 0;
    }

    private static Label Section(string text, Point at) => new()
    {
        Text = text, Font = Pv.Label, ForeColor = Pv.Orange, Location = at, AutoSize = true,
    };

    private static void StyleCombo(ComboBox c, Point at)
    {
        c.Location = at;
        c.Width = 412;
        c.DropDownStyle = ComboBoxStyle.DropDownList;
        c.FlatStyle = FlatStyle.Flat;
        c.BackColor = Pv.Charcoal;
        c.ForeColor = Pv.Bone;
        c.Font = Pv.Body;
    }

    /// <summary>Abre o mic escolhido so pra mostrar a barrinha de nivel.</summary>
    private void RestartMonitor()
    {
        _monitor?.Dispose();
        _monitor = null;
        if (_mic.SelectedItem is not InputDeviceItem it) return;
        try
        {
            _monitor = new VoiceEngine.MicMonitor(it.DeviceNumber);
            _monitor.LevelChanged += lvl =>
            {
                if (IsDisposed) return;
                try { BeginInvoke(() => { _level.Level = lvl; _level.Invalidate(); }); } catch { }
            };
        }
        catch (Exception ex) { Log.Write("teste de mic falhou: " + ex.Message); }
    }

    private void Apply()
    {
        if (_mic.SelectedItem is InputDeviceItem mi) _cfg.MicDevice = mi.DeviceNumber;
        if (_out.SelectedItem is OutputDeviceItem oi) _cfg.OutputDeviceId = oi.Id;
        _cfg.ClipHotkey = HotkeyBinding.Serialize(_hotkeyBox.Mods, _hotkeyBox.Key);
        _cfg.ClipSeconds = _secs.Value;
        _cfg.AutoBuffer = _autoBuf.Checked;
        if (_bw.SelectedIndex >= 0) _cfg.ScreenBudgetKb = BandwidthChoices[_bw.SelectedIndex].Kb;
        _cfg.ScreenFps = _fps.SelectedIndex switch { 2 => 15, 1 => 30, _ => 60 };
        _cfg.ScreenMaxWidth = _width.SelectedIndex switch { 2 => 1280, 1 => 2560, _ => 1920 };
        _cfg.TailscaleOnly = _tailnet.Checked;
        _cfg.UseSiteTheme = _siteTheme.Checked;
        _cfg.MusicVolume = _music.Value;
        _cfg.TrayOnClose = _tray.Checked;
        _cfg.JoinLeaveSound = _joinSound.Checked;
        _cfg.Save();
        Applied?.Invoke();
        Close();
    }

    /// <summary>
    /// Campo que captura uma combinacao de teclas: clica, aperta, pronto.
    /// </summary>
    private sealed class HotkeyBox : Control
    {
        private bool _capturing;

        public HotkeyBinding.Mods Mods { get; private set; }
        public Keys Key { get; private set; } = Keys.F9;

        public HotkeyBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            BackColor = Pv.Charcoal;
        }

        public void SetCombo(HotkeyBinding.Mods m, Keys k) { Mods = m; Key = k; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            _capturing = true;
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            _capturing = false;
            Invalidate();
            base.OnLostFocus(e);
        }

        // As teclas de atalho tipicas (F9, Tab, setas) sao "de navegacao" pro WinForms
        // e nao chegariam no OnKeyDown sem isto.
        protected override bool IsInputKey(Keys keyData) => true;

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (!_capturing) { base.OnKeyDown(e); return; }
            e.SuppressKeyPress = true;
            e.Handled = true;

            var k = e.KeyCode;
            if (HotkeyBinding.IsModifierOnly(k)) return;   // espera a tecla de verdade
            if (k == Keys.Escape) { _capturing = false; Invalidate(); return; }

            Mods = HotkeyBinding.ModsFrom(e.Modifiers);
            Key = k;
            _capturing = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var b = new SolidBrush(Pv.Charcoal)) g.FillRectangle(b, r);
            using (var p = new Pen(_capturing ? Pv.Orange : Pv.Char3, 2)) g.DrawRectangle(p, r);

            string text = _capturing ? "aperta a combinacao..." : HotkeyBinding.Format(Mods, Key);
            using var tb = new SolidBrush(_capturing ? Pv.Orange : Pv.Bone);
            float w = Pv.TrackedWidth(g, text, Pv.BodyBold, 1.2f);
            Pv.DrawTracked(g, text, Pv.BodyBold, tb, (Width - w) / 2f, (Height - Pv.BodyBold.Height) / 2f, 1.2f);
        }
    }

    /// <summary>Barrinha de nivel do microfone.</summary>
    private sealed class LevelBar : Control
    {
        public float Level;

        public LevelBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var b = new SolidBrush(Pv.Charcoal)) g.FillRectangle(b, r);
            using (var p = new Pen(Pv.Char3, 1)) g.DrawRectangle(p, r);

            int w = (int)(Math.Min(1f, Level * 3f) * (Width - 4));
            if (w <= 0) return;
            using var fill = new SolidBrush(Level > 0.5f ? Pv.Red : Pv.Green);
            g.FillRectangle(fill, 2, 2, w, Height - 5);
        }
    }
}
