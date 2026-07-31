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
        ClientSize = new Size(460, 560);
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

        var save = new PrimButton("SALVAR") { Location = new Point(24, 506), Size = new Size(200, 40) };
        save.Click += (_, _) => Apply();
        var cancel = new PrimButton("CANCELAR", PrimButton.Style.Ghost)
        { Location = new Point(236, 506), Size = new Size(200, 40) };
        cancel.Click += (_, _) => Close();

        Controls.AddRange(new Control[]
            { title, lblMic, _mic, lblTest, _level, lblOut, _out, warn,
              titleClip, lblKey, _hotkeyBox, lblSecs, _secs, _secsLabel, _autoBuf,
              save, cancel });

        Shown += (_, _) => RestartMonitor();
        FormClosed += (_, _) => { _monitor?.Dispose(); _monitor = null; };
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
