namespace Primicord;

/// <summary>Viewing state is independent of publishing a screen or joining voice.</summary>
public sealed partial class MainForm
{
    private Panel? _navigationRail;
    private TableLayoutPanel? _watchStages;
    private StageView? _secondStage;
    private uint? _secondSharer;
    private PrimButton? _pickWatch, _compareWatch, _expandWatch;
    private bool _watchFullscreen;
    private Rectangle _beforeWatchBounds;
    private Size _beforeWatchClientSize;
    private FormWindowState _beforeWatchState;
    private FormBorderStyle _beforeWatchBorder;
    private bool _beforeWatchMembers, _beforeWatchBanner;

    private List<(uint Id, string Name)> WatchSources()
    {
        var sources = new List<(uint, string)>();
        if (_iAmSharing || _previewSharing) sources.Add((0, "Minha transmissão"));
        if (_preview && _previewSharing)
        {
            sources.Add((101, "Mohamed · prévia"));
            sources.Add((102, "Vitinho · prévia"));
        }
        else if (_session != null)
            sources.AddRange(_session.Peers.Where(p => p.Sharing).Select(p => (p.SenderId, p.Nick)));
        return sources;
    }

    private Panel BuildWatchHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Pv.Charcoal };
        var title = new Label { Dock = DockStyle.Fill, Text = "Call da tribo", Font = Pv.BodyBold,
            ForeColor = Pv.Bone, TextAlign = ContentAlignment.MiddleLeft };
        _expandWatch = new PrimButton("TELA CHEIA", PrimButton.Style.Ghost) { Dock = DockStyle.Right, Width = 138 };
        _compareWatch = new PrimButton("+ OUTRA TELA", PrimButton.Style.Ghost) { Dock = DockStyle.Right, Width = 134 };
        _pickWatch = new PrimButton("ASSISTIR", PrimButton.Style.Ghost) { Dock = DockStyle.Right, Width = 120 };
        _expandWatch.AccessibleName = "Tela cheia mantendo participantes; F11 ou Esc para voltar";
        _compareWatch.AccessibleName = "Adicionar outra transmissão ou fechar a segunda tela";
        _pickWatch.AccessibleName = "Escolher transmissão sem interromper minha tela ou voz";
        _expandWatch.Click += (_, _) => SetWatchFullscreen(!_watchFullscreen);
        _compareWatch.Click += (_, _) =>
        {
            if (_secondSharer.HasValue) { _secondSharer = null; _secondStage?.SetFrame(null); _secondStage?.SetSelfFrame(null); SyncStageLayout(); }
            else ShowWatchSources(_compareWatch, true);
        };
        _pickWatch.Click += (_, _) => ShowWatchSources(_pickWatch, false);
        header.Controls.Add(title);
        header.Controls.Add(_pickWatch);
        header.Controls.Add(_compareWatch);
        header.Controls.Add(_expandWatch);
        return header;
    }

    private void ShowWatchSources(Control anchor, bool add)
    {
        var menu = new ContextMenuStrip { BackColor = Pv.Char2, ForeColor = Pv.Bone, ShowImageMargin = false };
        foreach (var source in WatchSources())
        {
            if (add && source.Id == _focusedSharer) continue;
            var item = new ToolStripMenuItem(source.Name) { Checked = !add && source.Id == _focusedSharer };
            item.Click += (_, _) => WatchStream(source.Id, add);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new ToolStripMenuItem("Nenhuma outra transmissão ativa") { Enabled = false });
        menu.Closed += (_, _) => BeginInvoke(() => menu.Dispose());
        menu.Show(anchor, new Point(0, anchor.Height));
    }

    private void WatchStream(uint id, bool add)
    {
        if (!WatchSources().Any(source => source.Id == id)) return;
        if (add && id != _focusedSharer) _secondSharer = id;
        else
        {
            if (_secondSharer == id) _secondSharer = _focusedSharer;
            _focusedSharer = id;
        }
        SyncStageLayout();
        if (_preview) RefreshWatchPreview();
        else TickStage();
    }

    private void SyncWatchLayout()
    {
        if (_watchStages == null || _stage == null || _secondStage == null) return;
        var sources = WatchSources();
        if (_secondSharer.HasValue && !sources.Any(s => s.Id == _secondSharer))
        {
            _secondSharer = null;
            _secondStage.SetFrame(null);
            _secondStage.SetSelfFrame(null);
        }
        bool pair = _secondSharer.HasValue;
        bool stacked = pair && _watchStages.ClientSize.Width < 850;
        int columns = pair && !stacked ? 2 : 1;
        int rows = stacked ? 2 : 1;
        if (_watchStages.ColumnCount != columns || _watchStages.RowCount != rows ||
            _watchStages.Controls.Contains(_secondStage) != pair)
        {
            _watchStages.SuspendLayout();
            _watchStages.Controls.Remove(_secondStage);
            _watchStages.ColumnCount = columns;
            _watchStages.RowCount = rows;
            _watchStages.ColumnStyles.Clear();
            _watchStages.RowStyles.Clear();
            for (int i = 0; i < columns; i++) _watchStages.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
            for (int i = 0; i < rows; i++) _watchStages.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));
            if (pair) _watchStages.Controls.Add(_secondStage, stacked ? 0 : 1, stacked ? 1 : 0);
            _watchStages.ResumeLayout();
        }
        _secondStage.Visible = pair;
        if (_expandWatch != null)
        {
            _expandWatch.Enabled = sources.Count > 0 || _watchFullscreen;
            _expandWatch.Text = _watchFullscreen ? "VOLTAR · ESC" : sources.Count > 0 ? "TELA CHEIA" : "SEM TRANSMISSÃO";
        }
        if (_pickWatch != null) { _pickWatch.Enabled = sources.Count > 0; _pickWatch.Text = sources.Count > 0 ? "ASSISTIR · " + sources.Count : "SEM TELAS"; }
        if (_compareWatch != null)
        {
            _compareWatch.Enabled = pair || sources.Count > 1;
            _compareWatch.Text = pair ? "FECHAR 2ª TELA" : sources.Count > 1 ? "+ OUTRA TELA" : "SEM OUTRA TELA";
        }
    }

    private void RefreshSecondStage()
    {
        if (_secondStage == null || !_secondSharer.HasValue) return;
        uint id = _secondSharer.Value;
        if (!WatchSources().Any(s => s.Id == id))
        {
            _secondSharer = null;
            _secondStage.SetFrame(null);
            _secondStage.SetSelfFrame(null);
            SyncStageLayout();
            return;
        }
        _secondStage.SelfPreview = id == 0;
        _secondStage.SharerNick = _session?.Peers.FirstOrDefault(p => p.SenderId == id)?.Nick ?? "";
        _secondStage.StatusRight = "2ª TELA";
        if (id != 0) _secondStage.SetFrame(_screens.FrameOf(id));
        _secondStage.Invalidate();
    }

    private void RefreshWatchPreview()
    {
        if (!_preview || _stage == null || !_previewSharing) return;
        void PaintPreview(StageView view, uint id)
        {
            view.SelfPreview = id == 0;
            view.SharerNick = WatchSources().FirstOrDefault(s => s.Id == id).Name ?? "Prévia";
            var bitmap = BuildPreviewFrame();
            using (var g = Graphics.FromImage(bitmap))
            using (var brush = new SolidBrush(Pv.Bone))
                g.DrawString(view.SharerNick + " · DADOS DE TESTE", Pv.Display, brush, 40, 190);
            if (id == 0) view.SetSelfFrame(bitmap); else view.SetFrame(bitmap);
        }
        PaintPreview(_stage, _focusedSharer);
        if (_secondSharer.HasValue && _secondStage != null) PaintPreview(_secondStage, _secondSharer.Value);
    }

    private void SetWatchFullscreen(bool enabled)
    {
        if (enabled == _watchFullscreen || enabled && WatchSources().Count == 0) return;
        if (enabled)
        {
            _beforeWatchState = WindowState;
            _beforeWatchBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            _beforeWatchClientSize = ClientSize;
            _beforeWatchBorder = FormBorderStyle;
            _beforeWatchMembers = _membersPanel?.Visible == true;
            _beforeWatchBanner = _banner.Visible;
        }
        _watchFullscreen = enabled;
        SuspendLayout();
        if (_navigationRail != null) _navigationRail.Visible = !enabled;
        if (_membersPanel != null) _membersPanel.Visible = !enabled && _beforeWatchMembers;
        if (_shellTitle != null) _shellTitle.Visible = !enabled;
        _banner.Visible = !enabled && _beforeWatchBanner;
        WindowState = FormWindowState.Normal;
        FormBorderStyle = enabled ? FormBorderStyle.None : _beforeWatchBorder;
        if (enabled) Bounds = Screen.FromControl(this).Bounds;
        else
        {
            Bounds = _beforeWatchBounds;
            if (_beforeWatchState == FormWindowState.Normal) ClientSize = _beforeWatchClientSize;
            WindowState = _beforeWatchState;
        }
        ResumeLayout(true);
        SyncStageLayout();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && _watchFullscreen) { SetWatchFullscreen(false); return true; }
        if (keyData == Keys.F11 && _roomPanel?.Visible == true && WatchSources().Count > 0)
        { SetWatchFullscreen(!_watchFullscreen); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
