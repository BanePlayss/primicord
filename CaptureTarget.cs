using System.Runtime.InteropServices;
using System.Text;

namespace Primicord;

/// <summary>O que esta sendo capturado: um monitor inteiro ou uma janela.</summary>
public sealed class CaptureTarget
{
    public string Name = "";
    public IntPtr Window = IntPtr.Zero;      // Zero = monitor
    public Rectangle MonitorBounds;

    public bool IsWindow => Window != IntPtr.Zero;

    public override string ToString() => Name;

    // ─── WIN32 ───────────────────────────────────────────────────────────────

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out Rect val, int size);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    private const int DwmaCloaked = 14;
    private const int DwmaExtendedFrameBounds = 9;

    /// <summary>PW_RENDERFULLCONTENT — sem isso Chrome/Electron saem pretos.</summary>
    private const uint PwRenderFullContent = 0x00000002;

    // ─── LISTAGEM ────────────────────────────────────────────────────────────

    /// <summary>Monitores + janelas abertas, no estilo do seletor do OBS.</summary>
    public static List<CaptureTarget> List()
    {
        var list = new List<CaptureTarget>();

        for (int i = 0; i < Screen.AllScreens.Length; i++)
        {
            var s = Screen.AllScreens[i];
            list.Add(new CaptureTarget
            {
                Name = $"[TELA] Monitor {i + 1} — {s.Bounds.Width}x{s.Bounds.Height}"
                     + (s.Primary ? " (principal)" : ""),
                MonitorBounds = s.Bounds,
            });
        }

        var shell = GetShellWindow();
        uint self = (uint)Environment.ProcessId;
        var windows = new List<CaptureTarget>();

        EnumWindows((h, _) =>
        {
            try
            {
                if (h == shell || !IsWindowVisible(h) || IsIconic(h)) return true;

                // Janela "cloaked" = existe mas nao aparece (app UWP suspenso,
                // area de trabalho virtual escondida). Nao serve pra capturar.
                if (DwmGetWindowAttribute(h, DwmaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                    return true;

                GetWindowThreadProcessId(h, out uint pid);
                if (pid == self) return true;      // nao oferecer o proprio Primicord

                int len = GetWindowTextLength(h);
                if (len <= 0) return true;
                var sb = new StringBuilder(len + 1);
                GetWindowText(h, sb, sb.Capacity);
                string title = sb.ToString().Trim();
                if (title.Length == 0) return true;

                if (!GetClientRect(h, out var rc)) return true;
                int w = rc.Right - rc.Left, hh = rc.Bottom - rc.Top;
                if (w < 200 || hh < 120) return true;   // barras, tooltips, lixo

                windows.Add(new CaptureTarget
                {
                    Name = $"[JANELA] {(title.Length > 52 ? title[..52] + "…" : title)} — {w}x{hh}",
                    Window = h,
                });
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        list.AddRange(windows);
        return list;
    }

    /// <summary>Tamanho atual do alvo (a janela pode ser redimensionada a qualquer hora).</summary>
    public Size CurrentSize()
    {
        if (!IsWindow) return MonitorBounds.Size;
        if (GetClientRect(Window, out var rc))
        {
            var s = new Size(rc.Right - rc.Left, rc.Bottom - rc.Top);
            if (s.Width > 0 && s.Height > 0) return s;
        }
        return Size.Empty;
    }

    public bool StillAlive() => !IsWindow || (IsWindowVisible(Window) && !IsIconic(Window));

    /// <summary>
    /// Copia o alvo pro bitmap. Monitor usa CopyFromScreen; janela usa PrintWindow com
    /// PW_RENDERFULLCONTENT, que pega o conteudo mesmo com outra janela por cima.
    /// </summary>
    public bool CaptureInto(Bitmap target, Graphics g)
    {
        try
        {
            if (!IsWindow)
            {
                g.CopyFromScreen(MonitorBounds.X, MonitorBounds.Y, 0, 0,
                                 new Size(target.Width, target.Height), CopyPixelOperation.SourceCopy);
                return true;
            }

            IntPtr hdc = g.GetHdc();
            try { return PrintWindow(Window, hdc, PwRenderFullContent); }
            finally { g.ReleaseHdc(hdc); }
        }
        catch (Exception ex)
        {
            Log.Write("captura falhou: " + ex.Message);
            return false;
        }
    }

    /// <summary>Posicao do alvo na tela — usada pra desenhar o cursor no lugar certo.</summary>
    public Rectangle ScreenRect()
    {
        if (!IsWindow) return MonitorBounds;
        // ExtendedFrameBounds da a borda REAL (o GetWindowRect inclui a sombra).
        if (DwmGetWindowAttribute(Window, DwmaExtendedFrameBounds, out Rect r, Marshal.SizeOf<Rect>()) == 0)
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        if (GetWindowRect(Window, out var wr))
            return Rectangle.FromLTRB(wr.Left, wr.Top, wr.Right, wr.Bottom);
        return Rectangle.Empty;
    }
}
