using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;

namespace Primicord;

/// <summary>
/// A sala de cinema: escolher o filme, acompanhar a transferencia e assistir junto.
/// </summary>
/// <remarks>
/// Usa libVLC (LibVLCSharp) porque precisa tocar QUALQUER formato que o pessoal
/// tenha (mkv, avi, mp4 com legenda embutida) e dar posicao precisa pra sincronia.
/// Os binarios do VLC vao embutidos no exe — sobe o tamanho, mas ninguem precisa
/// instalar nada.
///
/// SINCRONIA: so o host manda play/pause/seek. Quem assiste segue, e se a posicao
/// dele se afastar mais de 1,5s da do host, corrige sozinho. Sem isso cada um
/// derivaria alguns segundos por causa de buffer e velocidade de decodificacao.
/// </remarks>
public sealed class CinemaView : Panel
{
    private static LibVLC? _libVlc;
    private static bool _vlcBroken;

    private readonly CinemaSession _cinema;
    private readonly bool _isHost;

    private VideoView? _video;
    private MediaPlayer? _player;

    private readonly Panel _overlay = new() { Dock = DockStyle.Fill, BackColor = Pv.Charcoal };
    private readonly Label _status = new();
    private readonly ProgressPill _progress = new();
    private readonly PrimButton _pickBtn = new("ESCOLHER VIDEO");
    private EventHandler _pickHandler = (_, _) => { };
    private readonly Panel _controls = new() { Dock = DockStyle.Bottom, Height = 58, BackColor = Pv.Charcoal };
    private PrimButton? _playBtn;
    private Label? _time;
    private System.Windows.Forms.Timer? _syncTimer;

    private double _hostPosition;
    private long _hostStampMs;
    private bool _hostPlaying;

    public CinemaView(CinemaSession cinema, bool isHost)
    {
        _cinema = cinema;
        _isHost = isHost;
        BackColor = Pv.Charcoal;

        _status.Font = Pv.DisplaySm;
        _status.ForeColor = Pv.Bone;
        _status.AutoSize = true;
        _progress.Size = new Size(420, 10);
        _pickBtn.Size = new Size(260, 42);
        _pickHandler = async (_, _) => await PickFileAsync();
        _pickBtn.Click += _pickHandler;

        _overlay.Controls.AddRange(new Control[] { _status, _progress, _pickBtn });
        _overlay.Resize += (_, _) => LayoutOverlay();

        BuildControls();
        Controls.Add(_overlay);
        Controls.Add(_controls);

        _cinema.Changed += OnCinemaChanged;
        _cinema.Failed += msg => { try { BeginInvoke(() => ShowMessage(msg)); } catch { } };
        _cinema.PlaybackCommand += OnHostCommand;

        OnCinemaChanged();
    }

    private void LayoutOverlay()
    {
        int cy = _overlay.ClientSize.Height / 2;
        _status.Location = new Point((_overlay.ClientSize.Width - _status.Width) / 2, cy - 60);
        _progress.Location = new Point((_overlay.ClientSize.Width - _progress.Width) / 2, cy - 16);
        _pickBtn.Location = new Point((_overlay.ClientSize.Width - _pickBtn.Width) / 2, cy + 16);
    }

    private void BuildControls()
    {
        _controls.Paint += (_, e) =>
        {
            using var p = new Pen(Pv.Char3, 2);
            e.Graphics.DrawLine(p, 16, 0, _controls.Width - 16, 0);
        };

        _playBtn = new PrimButton("PLAY") { Size = new Size(120, 36) };
        _playBtn.Click += (_, _) => TogglePlay();

        _time = new Label { Font = Pv.Mono, ForeColor = Pv.BoneDim, AutoSize = true, Text = "00:00" };

        void Layout()
        {
            int y = (_controls.ClientSize.Height - 36) / 2;
            _playBtn.Location = new Point(16, y);
            _time.Location = new Point(148, y + 10);
        }
        _controls.Resize += (_, _) => Layout();
        _controls.Controls.AddRange(new Control[] { _playBtn, _time });
        Layout();
        _controls.Visible = false;
    }

    // ─── ESCOLHER O FILME ────────────────────────────────────────────────────

    private async Task PickFileAsync()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Escolhe o video pra sessao",
            Filter = "Videos|*.mp4;*.mkv;*.avi;*.mov;*.webm;*.m4v;*.wmv|Todos os arquivos|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var fi = new FileInfo(dlg.FileName);
        if (fi.Length > 2L * 1024 * 1024 * 1024)
        {
            var r = MessageBox.Show(
                $"O arquivo tem {fi.Length / 1024 / 1024} MB. Vai demorar pra todo mundo baixar. Continuar?",
                "PRIMICORD", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;
        }
        await _cinema.StartHostingAsync(dlg.FileName);
    }

    // ─── ESTADO ──────────────────────────────────────────────────────────────

    private void OnCinemaChanged()
    {
        if (InvokeRequired) { try { BeginInvoke(OnCinemaChanged); } catch { } return; }
        if (IsDisposed) return;

        switch (_cinema.State)
        {
            case CinemaSession.Phase.Ocioso:
                _status.Text = _isHost ? "NENHUM VIDEO NA SESSAO" : "ESPERANDO O HOST ESCOLHER";
                _progress.Visible = false;
                _pickBtn.Visible = _isHost;
                break;

            case CinemaSession.Phase.Enviando:
                _status.Text = $"ENVIANDO {_cinema.FileName}";
                _progress.Visible = true;
                _progress.Percent = _cinema.Percent;
                _pickBtn.Visible = false;
                break;

            case CinemaSession.Phase.Recebendo:
                _status.Text = $"BAIXANDO DE {_cinema.HostNick.ToUpperInvariant()} — {_cinema.Percent}%";
                _progress.Visible = true;
                _progress.Percent = _cinema.Percent;
                _pickBtn.Visible = false;
                break;

            case CinemaSession.Phase.Pronto:
            case CinemaSession.Phase.Tocando:
                _status.Text = "PRONTO";
                _progress.Visible = false;
                _pickBtn.Visible = false;
                StartPlayback();
                break;
        }
        _progress.Invalidate();
        LayoutOverlay();
    }

    private void ShowMessage(string msg)
    {
        _status.Text = msg;
        _progress.Visible = false;
        _pickBtn.Visible = _isHost;
        LayoutOverlay();
    }

    // ─── PLAYER ──────────────────────────────────────────────────────────────

    private void StartPlayback()
    {
        if (_player != null || _cinema.LocalPath == null) return;

        var vlc = EnsureVlc();
        if (vlc == null)
        {
            // Degrada com utilidade em vez de so falhar: o arquivo ja esta baixado,
            // entao oferece abrir no player do sistema.
            _progress.Visible = false;
            _status.Text = "O PLAYER INTERNO NAO CARREGOU";
            _pickBtn.Text = "ABRIR NO PLAYER DO WINDOWS";
            _pickBtn.Visible = true;
            _pickBtn.Click -= _pickHandler;
            _pickBtn.Click += (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    { FileName = _cinema.LocalPath!, UseShellExecute = true });
                }
                catch (Exception ex) { Log.Write("abrir no player externo: " + ex.Message); }
            };
            LayoutOverlay();
            return;
        }

        try
        {
            _video = new VideoView { Dock = DockStyle.Fill, BackColor = Color.Black };
            _player = new MediaPlayer(vlc);
            _video.MediaPlayer = _player;

            Controls.Add(_video);
            _video.BringToFront();
            _controls.Visible = true;
            _controls.BringToFront();
            _overlay.Visible = false;

            using var media = new Media(vlc, new Uri(_cinema.LocalPath));
            _player.Play(media);
            // Host comanda; quem assiste comeca pausado ate o host mandar tocar.
            if (!_isHost) _player.SetPause(true);

            _syncTimer?.Dispose();
            _syncTimer = new System.Windows.Forms.Timer { Interval = 500 };
            _syncTimer.Tick += (_, _) => SyncTick();
            _syncTimer.Start();

            Log.Write("cinema: tocando " + _cinema.LocalPath);
        }
        catch (Exception ex)
        {
            Log.Write("cinema: player falhou: " + ex.Message);
            ShowMessage("Nao consegui tocar o video: " + ex.Message);
        }
    }

    /// <summary>
    /// Carrega o libVLC uma vez por processo, instalando o motor se for a 1a vez.
    /// </summary>
    private LibVLC? EnsureVlc()
    {
        if (_libVlc != null) return _libVlc;
        if (_vlcBroken) return null;
        try
        {
            if (!VlcRuntime.Installed)
            {
                _status.Text = "PREPARANDO O PLAYER (so na primeira vez)";
                _progress.Visible = true;
                _progress.Percent = 0;
                LayoutOverlay();
                Application.DoEvents();
            }

            string? dir = VlcRuntime.Ensure(pct =>
            {
                if (IsDisposed) return;
                try
                {
                    BeginInvoke(() =>
                    {
                        _progress.Percent = pct;
                        _progress.Invalidate();
                    });
                }
                catch { }
            });
            if (dir == null) { _vlcBroken = true; return null; }

            // Aponta pra pasta instalada: sem isso o LibVLCSharp procura ao lado do
            // exe, onde nada existe no publish de arquivo unico.
            Core.Initialize(dir);
            _libVlc = new LibVLC("--no-osd", "--quiet");
            return _libVlc;
        }
        catch (Exception ex)
        {
            _vlcBroken = true;
            Log.Write("cinema: libVLC nao carregou: " + ex.Message);
            return null;
        }
    }

    private void TogglePlay()
    {
        if (_player == null) return;
        if (!_isHost) return;      // so o host comanda

        bool willPlay = !_player.IsPlaying;
        double pos = _player.Time / 1000.0;
        if (willPlay) _player.Play(); else _player.SetPause(true);
        _cinema.SendPlayback(willPlay ? "play" : "pause", pos);
        if (_playBtn != null) { _playBtn.Text = willPlay ? "PAUSAR" : "PLAY"; _playBtn.Invalidate(); }
    }

    private void OnHostCommand(string action, double position)
    {
        if (InvokeRequired) { try { BeginInvoke(() => OnHostCommand(action, position)); } catch { } return; }
        _hostPosition = position;
        _hostStampMs = Environment.TickCount64;
        _hostPlaying = action != "pause";

        if (_player == null || _isHost) return;
        switch (action)
        {
            case "play":
                _player.Time = (long)(position * 1000);
                _player.Play();
                break;
            case "pause":
                _player.SetPause(true);
                _player.Time = (long)(position * 1000);
                break;
            case "seek":
                _player.Time = (long)(position * 1000);
                break;
        }
        if (_playBtn != null) { _playBtn.Text = _hostPlaying ? "TOCANDO" : "PAUSADO"; _playBtn.Invalidate(); }
    }

    /// <summary>
    /// A cada 500ms: o host anuncia onde esta; quem assiste corrige se derivou.
    /// </summary>
    private void SyncTick()
    {
        if (_player == null || IsDisposed) return;
        double me = _player.Time / 1000.0;

        if (_time != null)
            _time.Text = TimeSpan.FromSeconds(Math.Max(0, me)).ToString(@"hh\:mm\:ss");

        if (_isHost)
        {
            if (_player.IsPlaying) _cinema.SendPlayback("seek", me);
            return;
        }

        if (!_hostPlaying) return;
        // Onde o host deve estar AGORA, contando o tempo desde o ultimo aviso.
        double expected = _hostPosition + (Environment.TickCount64 - _hostStampMs) / 1000.0;
        double drift = Math.Abs(me - expected);
        if (drift > 1.5)
        {
            _player.Time = (long)(expected * 1000);
            Log.Write($"cinema: corrigindo sincronia ({drift:0.0}s de diferenca)");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _syncTimer?.Stop(); _syncTimer?.Dispose(); } catch { }
            try { _player?.Stop(); _player?.Dispose(); } catch { }
            try { _video?.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }

    /// <summary>Barrinha de progresso da transferencia.</summary>
    private sealed class ProgressPill : Control
    {
        public int Percent;

        public ProgressPill()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Pv.Charcoal;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var b = new SolidBrush(Pv.Char3))
            using (var p = Pv.RoundRect(r, Height / 2))
                g.FillPath(b, p);
            int w = (int)(Width * Math.Clamp(Percent, 0, 100) / 100.0);
            if (w < 4) return;
            using (var b = new SolidBrush(Pv.Orange))
            using (var p = Pv.RoundRect(new Rectangle(0, 0, w, Height - 1), Height / 2))
                g.FillPath(b, p);
        }
    }
}
