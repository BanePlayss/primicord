using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace Primicord;

// Original native drawing, inspired by the interaction patterns in Magic UI.
// No web runtime and no perpetual animation: only hover and activity changes animate.
internal sealed class GameActivityCard : Control
{
    private readonly MotionValue _hover, _reveal;
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 10000 };
    private readonly bool _preview;
    private bool _busy, _automatic = true, _hidden;
    private int? _selectedProcess;
    private long _revision;
    private string _game = "";
    private Bitmap? _icon;
    private long _started;
    private readonly ToolTip _tip = new();
    public string Game => _game;
    public event Action<string>? ActivityChanged;

    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["League of Legends"] = "League of Legends",
        ["VALORANT-Win64-Shipping"] = "VALORANT", ["cs2"] = "Counter-Strike 2",
        ["Minecraft.Windows"] = "Minecraft", ["RustClient"] = "Rust",
        ["RocketLeague"] = "Rocket League", ["FortniteClient-Win64-Shipping"] = "Fortnite",
        ["r5apex"] = "Apex Legends", ["dota2"] = "Dota 2", ["Overwatch"] = "Overwatch 2",
        ["GTA5"] = "Grand Theft Auto V", ["eldenring"] = "ELDEN RING",
        ["RobloxPlayerBeta"] = "Roblox", ["Terraria"] = "Terraria", ["Stardew Valley"] = "Stardew Valley",
    };

    public GameActivityCard(bool preview)
    {
        _preview = preview;
        _hover = new MotionValue(this); _reveal = new MotionValue(this, 1);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 92; BackColor = Pv.SurfaceLowest; Cursor = Cursors.Hand; TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = "Atividade de jogo; Enter para escolher ou ocultar";
        _tip.SetToolTip(this, "Escolher jogo, editar atividade ou ocultar");
        _refresh.Tick += async (_, _) => { await RefreshGameAsync(); Invalidate(); };
        if (preview) SetActivity("League of Legends", null);
    }

    protected override async void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!_preview) { _refresh.Start(); await RefreshGameAsync(); }
    }

    private sealed record RunningGame(int Id, string Name, string? Path)
    {
        public override string ToString() => Name;
    }

    private static List<RunningGame> Running(bool knownOnly, int? preferred = null)
    {
        var games = new List<RunningGame>();
        foreach (var process in Process.GetProcesses())
        using (process)
        {
            try
            {
                if (process.Id == Environment.ProcessId) continue;
                string key = process.ProcessName;
                string title = process.MainWindowTitle;
                bool known = Known.TryGetValue(key, out var name);
                // Java alone is not evidence of Minecraft. Require its visible window title.
                if (!known && (key is "javaw" or "java") && title.StartsWith("Minecraft", StringComparison.OrdinalIgnoreCase))
                { known = true; name = "Minecraft"; }
                if (knownOnly && !known && process.Id != preferred) continue;
                if (!knownOnly && process.MainWindowHandle == IntPtr.Zero) continue;
                if (preferred.HasValue && process.Id != preferred) continue;
                // Window titles can contain document names. Publish only an explicit game label or process name.
                string? path = null;
                try { path = process.MainModule?.FileName; } catch { }
                games.Add(new(process.Id, name ?? key, path));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }
        return games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Bitmap? ExtractIcon(string? path)
    {
        if (path == null) return null;
        try { using var icon = Icon.ExtractAssociatedIcon(path); return icon?.ToBitmap(); }
        catch { return null; }
    }

    private async Task RefreshGameAsync()
    {
        if (_busy || _hidden || (!_automatic && !_selectedProcess.HasValue) || IsDisposed) return;
        _busy = true;
        long revision = _revision;
        int? selected = _selectedProcess;
        string previousGame = _game;
        bool needsIcon = _icon == null;
        try
        {
            var result = await Task.Run(() =>
            {
                var games = Running(true, selected);
                var game = games.FirstOrDefault(g => g.Name == previousGame) ?? games.FirstOrDefault();
                return (game, icon: game != null && (needsIcon || game.Name != previousGame) ? ExtractIcon(game.Path) : null);
            });
            if (IsDisposed || revision != _revision) { result.icon?.Dispose(); return; }
            if (result.game == null) { SetActivity("", null); return; }
            SetActivity(result.game.Name, result.icon);
        }
        catch (Exception ex) { Log.Write("atividade local: " + ex.Message); }
        finally { _busy = false; }
    }

    private void SetActivity(string game, Bitmap? icon)
    {
        if (game == _game)
        {
            if (icon != null) { _icon?.Dispose(); _icon = icon; }
            Invalidate(); return;
        }
        _icon?.Dispose(); _icon = icon; _game = game;
        _started = Stopwatch.GetTimestamp();
        _reveal.Snap(0); _reveal.To(1, 650);
        AccessibleDescription = game.Length > 0 ? "Jogando " + game : "Nenhum jogo selecionado";
        ActivityChanged?.Invoke(game);
        Invalidate();
    }

    private void SelectMode(bool automatic, bool hidden = false)
    { _revision++; _automatic = automatic; _hidden = hidden; _selectedProcess = null; Invalidate(); }

    protected override void OnClick(EventArgs e) { base.OnClick(e); ShowActivityMenu(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space) { e.SuppressKeyPress = true; ShowActivityMenu(); }
        else base.OnKeyDown(e);
    }
    protected override void OnMouseEnter(EventArgs e) { _hover.To(1, 200); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover.To(0, 250); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    public void ShowActivityMenu()
    {
        var menu = new ContextMenuStrip { BackColor = Pv.Char2, ForeColor = Pv.Bone, ShowImageMargin = false };
        var auto = new ToolStripMenuItem("Detectar jogos conhecidos") { Checked = _automatic && !_hidden };
        auto.Click += async (_, _) => { SelectMode(true); await RefreshGameAsync(); };
        menu.Items.Add(auto);
        menu.Items.Add("Escolher aplicativo aberto…", null, async (_, _) =>
        {
            var games = await Task.Run(() => Running(false));
            if (IsDisposed) return;
            if (games.Count == 0) { MessageBox.Show(FindForm(), "Abra o jogo e tente novamente.", "Atividade"); return; }
            int choice = PickDialog.Choose(FindForm()!, "ATIVIDADE", "Escolha o jogo em execução", games.Select(g => g.Name).ToList(), "SELECIONAR");
            if (choice < 0) return;
            var chosen = games[choice];
            SelectMode(false); _selectedProcess = chosen.Id;
            long revision = _revision;
            var icon = await Task.Run(() => ExtractIcon(chosen.Path));
            if (IsDisposed || revision != _revision) { icon?.Dispose(); return; }
            SetActivity(chosen.Name, icon);
        });
        menu.Items.Add("Definir nome manualmente…", null, (_, _) =>
        {
            var game = PromptDialog.Ask(FindForm()!, "ATIVIDADE", "Nome do jogo (vazio para limpar)", _game, "SALVAR");
            if (game == null) return;
            SelectMode(false); SetActivity(game.Trim()[..Math.Min(60, game.Trim().Length)], null);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Ocultar minha atividade", null, (_, _) => { SelectMode(false, true); SetActivity("", null); });
        menu.Closed += (_, _) => { if (!IsDisposed) BeginInvoke(() => menu.Dispose()); else menu.Dispose(); };
        menu.Show(this, new Point(8, 0), ToolStripDropDownDirection.AboveRight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(8, 6, Math.Max(20, Width - 16), Height - 12);
        using var path = Pv.RoundRect(r, 10);
        using var fill = new LinearGradientBrush(r, Color.FromArgb(40, 35, 66), Pv.SurfaceLowest, 25f);
        g.FillPath(fill, path);
        using var edge = new Pen(UiMotion.Blend(Color.FromArgb(72, 62, 108), Color.FromArgb(163, 137, 255), _hover.Value), Focused ? 2 : 1);
        g.DrawPath(edge, path);
        // One light sweep on activity change. It stops after 650 ms.
        if (_reveal.Value < 1 && UiMotion.Enabled)
        {
            var state = g.Save(); g.SetClip(path);
            int x = r.X - 50 + (int)((r.Width + 100) * _reveal.Value);
            using var shine = new LinearGradientBrush(new Rectangle(x, r.Y, 50, r.Height), Color.Transparent, Color.FromArgb(40, 190, 165, 255), 0f);
            g.FillRectangle(shine, x, r.Y, 50, r.Height); g.Restore(state);
        }
        var iconBox = new Rectangle(r.X + 12, r.Y + 22, 38, 38);
        using (var tile = Pv.RoundRect(iconBox, 8))
        using (var brush = new SolidBrush(Color.FromArgb(63, 53, 91))) g.FillPath(brush, tile);
        if (_icon != null) g.DrawImage(_icon, Rectangle.Inflate(iconBox, -3, -3));
        else Glyphs.Gamepad(g, RectangleF.Inflate(iconBox, -7, -7), Color.FromArgb(204, 190, 255));
        int tx = iconBox.Right + 10;
        var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(g, _game.Length > 0 ? "JOGANDO AGORA" : "SUA ATIVIDADE", Pv.Label,
            new Rectangle(tx, r.Y + 12, r.Right - tx - 10, 16), Color.FromArgb(186, 164, 245), flags);
        TextRenderer.DrawText(g, _game.Length > 0 ? _game : _hidden ? "Atividade oculta" : "Escolher jogo", Pv.BodyBold,
            new Rectangle(tx, r.Y + 30, r.Right - tx - 10, 21), Pv.Bone, flags);
        var elapsed = Stopwatch.GetElapsedTime(_started);
        string detail = _game.Length == 0 ? "Clique para configurar" : _preview ? "Prévia de atividade" :
            $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00} de atividade · {(_automatic ? "automático" : "manual")}";
        TextRenderer.DrawText(g, detail, Pv.Label, new Rectangle(tx, r.Y + 54, r.Right - tx - 10, 16), Pv.BoneDim, flags);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _revision++; _refresh.Stop(); _refresh.Dispose(); _icon?.Dispose(); _tip.Dispose(); }
        base.Dispose(disposing);
    }
}
