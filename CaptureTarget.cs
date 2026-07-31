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

    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(
        IntPtr hdcDest, int xd, int yd, int wd, int hd,
        IntPtr hdcSrc, int xs, int ys, int ws, int hs, uint rop);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr p);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);

    private const uint SrcCopy = 0x00CC0020;

    /// <summary>
    /// HALFTONE faz media dos pixels (texto nitido) mas custa caro; COLORONCOLOR
    /// so descarta pixels e e bem mais rapido. Medido nesta maquina reduzindo
    /// 1920x1080 -> 1024x576: HALFTONE 35ms, COLORONCOLOR 21ms. A diferenca vale
    /// uns 10 fps, entao a escolha e feita quadro a quadro conforme a cena.
    /// </summary>
    private const int Halftone = 4;
    private const int ColorOnColor = 3;

    // DC intermediario reusado pra captura de janela (criar a cada quadro custa caro).
    private IntPtr _winDc, _winBmp, _winOld;
    private Size _winDcSize;

    /// <summary>
    /// Captura JA NO TAMANHO FINAL, numa unica operacao.
    /// </summary>
    /// <remarks>
    /// Antes era capturar em 1920x1080 (CopyFromScreen, ~30ms) e depois reduzir com
    /// GDI+ bicubico (~24ms) — 54ms so nisso, o que travava tudo em 17fps. O StretchBlt
    /// faz copia e reducao de uma vez dentro do GDI, e o bitmap intermediario de 8MB
    /// por quadro deixa de existir.
    /// </remarks>
    /// <param name="fast">
    /// true = prioriza velocidade (cena com movimento, onde ninguem repara na
    /// suavidade); false = prioriza nitidez (tela parada, onde ler texto importa).
    /// </param>
    public bool CaptureScaledInto(Bitmap dest, Graphics gDest, bool fast = false)
    {
        try
        {
            var src = IsWindow ? CurrentSize() : MonitorBounds.Size;
            if (src.Width <= 0 || src.Height <= 0) return false;

            IntPtr hdcDest = gDest.GetHdc();
            try
            {
                SetStretchBltMode(hdcDest, fast ? ColorOnColor : Halftone);
                SetBrushOrgEx(hdcDest, 0, 0, IntPtr.Zero);

                if (!IsWindow)
                {
                    IntPtr desktop = GetDesktopWindow();
                    IntPtr hdcSrc = GetWindowDC(desktop);
                    try
                    {
                        return StretchBlt(hdcDest, 0, 0, dest.Width, dest.Height,
                                          hdcSrc, MonitorBounds.X, MonitorBounds.Y,
                                          src.Width, src.Height, SrcCopy);
                    }
                    finally { ReleaseDC(desktop, hdcSrc); }
                }

                // Janela: PrintWindow precisa de um DC do tamanho nativo; reduzimos
                // dele pro destino. O DC e reaproveitado entre quadros.
                EnsureWindowDc(hdcDest, src);
                if (_winDc == IntPtr.Zero) return false;
                if (!PrintWindow(Window, _winDc, PwRenderFullContent)) return false;
                return StretchBlt(hdcDest, 0, 0, dest.Width, dest.Height,
                                  _winDc, 0, 0, src.Width, src.Height, SrcCopy);
            }
            finally { gDest.ReleaseHdc(hdcDest); }
        }
        catch (Exception ex)
        {
            Log.Write("captura falhou: " + ex.Message);
            return false;
        }
    }

    private void EnsureWindowDc(IntPtr reference, Size size)
    {
        if (_winDc != IntPtr.Zero && _winDcSize == size) return;
        ReleaseWindowDc();
        _winDc = CreateCompatibleDC(reference);
        if (_winDc == IntPtr.Zero) return;
        _winBmp = CreateCompatibleBitmap(reference, size.Width, size.Height);
        if (_winBmp == IntPtr.Zero) { DeleteDC(_winDc); _winDc = IntPtr.Zero; return; }
        _winOld = SelectObject(_winDc, _winBmp);
        _winDcSize = size;
    }

    public void ReleaseWindowDc()
    {
        if (_winDc == IntPtr.Zero) return;
        try { SelectObject(_winDc, _winOld); } catch { }
        try { DeleteObject(_winBmp); } catch { }
        try { DeleteDC(_winDc); } catch { }
        _winDc = _winBmp = _winOld = IntPtr.Zero;
        _winDcSize = Size.Empty;
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
