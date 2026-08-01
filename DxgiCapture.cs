using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Primicord;

/// <summary>
/// Captura de tela pela GPU (DXGI Desktop Duplication) — o mesmo caminho que o OBS
/// e o Discord usam.
/// </summary>
/// <remarks>
/// POR QUE: ler a tela pelo GDI (BitBlt/StretchBlt) custa 20-30ms nesta maquina, o
/// que sozinho trava tudo em ~30fps e era 81% do tempo de cada quadro. O Windows ja
/// mantem a area de trabalho composta na memoria da GPU; o Desktop Duplication
/// entrega essa superficie pronta, sem redesenhar nada.
///
/// O caminho e: AcquireNextFrame devolve uma textura na GPU -> copiamos pra uma
/// textura "staging" (visivel pela CPU) -> Map devolve um ponteiro pros pixels. O
/// unico custo real e essa copia GPU->CPU, que o driver faz por DMA.
///
/// EXIGE: Windows 8+ e o app rodando na mesma GPU do monitor. Falha esperada em
/// alguns casos (sessao bloqueada, troca de modo de video, jogo em tela cheia
/// exclusiva trocando de resolucao) — por isso todo erro cai de volta pro GDI em
/// vez de derrubar o compartilhamento.
/// </remarks>
public sealed class DxgiCapture : IDisposable
{
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _dup;
    private ID3D11Texture2D? _staging;

    private readonly Rectangle _bounds;
    /// <summary>Retangulo do monitor escolhido, em coordenadas da area de trabalho.</summary>
    private Rectangle _outputRect;
    private int _texW, _texH;

    /// <summary>Ultimo motivo de falha, pra logar quando cair pro GDI.</summary>
    public string? LastError { get; private set; }

    public bool Ready => _dup != null;

    private DxgiCapture(Rectangle bounds) => _bounds = bounds;

    /// <summary>
    /// Tenta abrir a duplicacao do monitor que contem o retangulo dado.
    /// Devolve null se nao der (o chamador segue no GDI).
    /// </summary>
    public static DxgiCapture? TryCreate(Rectangle monitorBounds)
    {
        var c = new DxgiCapture(monitorBounds);
        try
        {
            c.Init();
            Log.Write($"DXGI: duplicacao aberta para {monitorBounds.Width}x{monitorBounds.Height}");
            return c;
        }
        catch (Exception ex)
        {
            Log.Write("DXGI indisponivel, usando GDI: " + ex.Message);
            c.Dispose();
            return null;
        }
    }

    private void Init()
    {
        var featureLevels = new[]
        {
            FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1,
        };

        // BgraSupport deixa a textura compativel com o formato que o GDI espera.
        var hr = D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, featureLevels,
            out _device, out _, out _context);
        hr.CheckError();
        if (_device == null || _context == null) throw new InvalidOperationException("D3D11 nao criou");

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();

        // Acha a saida (monitor) cujo retangulo bate com o que queremos capturar.
        IDXGIOutput1? chosen = null;
        for (uint i = 0; ; i++)
        {
            var res = adapter.EnumOutputs(i, out IDXGIOutput? output);
            if (res.Failure || output == null) break;
            var desc = output.Description;
            var rect = Rectangle.FromLTRB(
                desc.DesktopCoordinates.Left, desc.DesktopCoordinates.Top,
                desc.DesktopCoordinates.Right, desc.DesktopCoordinates.Bottom);

            if (rect.IntersectsWith(_bounds) && chosen == null)
            {
                chosen = output.QueryInterface<IDXGIOutput1>();
                _outputRect = rect;
            }
            else output.Dispose();
        }
        if (chosen == null) throw new InvalidOperationException("nenhum monitor DXGI bate com a area");

        using (chosen)
        {
            _dup = chosen.DuplicateOutput(_device);
        }

        // A textura tem que ter o tamanho do MONITOR INTEIRO: o CopyResource exige
        // dimensoes identicas as do quadro entregue. Se a area pedida for menor
        // (sub-regiao ou monitor deslocado), o recorte acontece na hora de desenhar.
        _texW = _outputRect.Width;
        _texH = _outputRect.Height;
        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_texW,
            Height = (uint)_texH,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
    }

    /// <summary>
    /// Pega o quadro mais recente e reduz pro bitmap de destino.
    /// </summary>
    /// <returns>
    /// true = quadro novo desenhado; false = nada mudou desde a ultima chamada
    /// (o DXGI so entrega quando ha mudanca) ou deu erro recuperavel.
    /// </returns>
    public bool CaptureInto(Bitmap dest, Graphics gDest, bool fast, int timeoutMs = 12)
    {
        var dup = _dup;
        var ctx = _context;
        var staging = _staging;
        if (dup == null || ctx == null || staging == null) return false;

        IDXGIResource? resource = null;
        bool acquired = false;
        try
        {
            var res = dup.AcquireNextFrame((uint)timeoutMs, out OutduplFrameInfo info, out resource);
            if (res == Vortice.DXGI.ResultCode.WaitTimeout) return false;   // nada mudou
            if (res.Failure)
            {
                LastError = res.Description;
                // AccessLost = trocou de modo/resolucao: precisa reabrir tudo.
                if (res == Vortice.DXGI.ResultCode.AccessLost) Reset();
                return false;
            }
            acquired = true;
            if (resource == null) return false;

            using var tex = resource.QueryInterface<ID3D11Texture2D>();
            ctx.CopyResource(staging, tex);

            var map = ctx.Map(staging, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                // Copia as linhas pra uma DIB de verdade. Embrulhar a memoria mapeada
                // num Bitmap do GDI+ e pedir o HDC dele PARECE funcionar (nao da erro)
                // mas o blit sai todo PRETO — o DC que o GDI+ devolve nao enxerga a
                // memoria emprestada. A copia custa ~1ms e resolve.
                EnsureDib(_texW, _texH);
                if (_dibDc == IntPtr.Zero) return false;
                CopyRows(map.DataPointer, (int)map.RowPitch);
            }
            finally { ctx.Unmap(staging, 0); }

            // Recorte da area pedida dentro do monitor (0,0 quando e o monitor todo).
            var srcRect = new Rectangle(
                _bounds.X - _outputRect.X, _bounds.Y - _outputRect.Y,
                Math.Min(_bounds.Width, _texW), Math.Min(_bounds.Height, _texH));
            BlitFromDib(srcRect, dest, gDest, fast);

            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Write("DXGI: falha na captura: " + ex.Message);
            return false;
        }
        finally
        {
            resource?.Dispose();
            if (acquired) { try { dup.ReleaseFrame(); } catch { } }
        }
    }

    /// <summary>
    /// Reduz de memoria pra memoria. Aqui o StretchBlt e barato: nao ha leitura da
    /// GPU no meio, que era o custo real no caminho antigo.
    /// </summary>
    // ─── DIB intermediaria (a superficie GDI de onde o blit realmente le) ────

    private IntPtr _dibDc, _dibBmp, _dibOld, _dibBits;
    private int _dibW, _dibH, _dibStride;

    private void EnsureDib(int w, int h)
    {
        if (_dibDc != IntPtr.Zero && _dibW == w && _dibH == h) return;
        ReleaseDib();

        var bi = new BitmapInfoHeader
        {
            biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            biWidth = w,
            biHeight = -h,          // negativo = de cima pra baixo, igual ao DXGI
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,      // BI_RGB
        };
        _dibDc = CreateCompatibleDC(IntPtr.Zero);
        if (_dibDc == IntPtr.Zero) return;
        _dibBmp = CreateDIBSection(_dibDc, ref bi, 0, out _dibBits, IntPtr.Zero, 0);
        if (_dibBmp == IntPtr.Zero) { DeleteDC(_dibDc); _dibDc = IntPtr.Zero; return; }
        _dibOld = SelectObject(_dibDc, _dibBmp);
        _dibW = w; _dibH = h;
        _dibStride = w * 4;         // 32bpp sempre alinhado
    }

    private unsafe void CopyRows(IntPtr src, int srcPitch)
    {
        byte* s = (byte*)src;
        byte* d = (byte*)_dibBits;
        int bytes = Math.Min(srcPitch, _dibStride);
        for (int y = 0; y < _dibH; y++)
            Buffer.MemoryCopy(s + (long)y * srcPitch, d + (long)y * _dibStride, _dibStride, bytes);
    }

    private void BlitFromDib(Rectangle srcRect, Bitmap dest, Graphics gDest, bool fast)
    {
        IntPtr hdcDest = gDest.GetHdc();
        try
        {
            // Mesmo tamanho = copia direta, sem reamostrar. O custo do HALFTONE
            // cresce com o TAMANHO DE SAIDA: reduzir 1920->1600 chega a custar
            // mais que 1920->1024. Quando nao precisa reduzir, nao reduz.
            if (srcRect.Width == dest.Width && srcRect.Height == dest.Height)
            {
                BitBlt(hdcDest, 0, 0, dest.Width, dest.Height,
                       _dibDc, srcRect.X, srcRect.Y, 0x00CC0020);
                return;
            }
            SetStretchBltMode(hdcDest, fast ? 3 : 4);   // COLORONCOLOR : HALFTONE
            SetBrushOrgEx(hdcDest, 0, 0, IntPtr.Zero);
            StretchBlt(hdcDest, 0, 0, dest.Width, dest.Height,
                       _dibDc, srcRect.X, srcRect.Y, srcRect.Width, srcRect.Height, 0x00CC0020);
        }
        finally { gDest.ReleaseHdc(hdcDest); }
    }

    private void ReleaseDib()
    {
        if (_dibDc == IntPtr.Zero) return;
        try { SelectObject(_dibDc, _dibOld); } catch { }
        try { DeleteObject(_dibBmp); } catch { }
        try { DeleteDC(_dibDc); } catch { }
        _dibDc = _dibBmp = _dibOld = _dibBits = IntPtr.Zero;
        _dibW = _dibH = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BitmapInfoHeader bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);

    /// <summary>Reabre a duplicacao (usado quando o Windows tira o acesso).</summary>
    private void Reset()
    {
        Log.Write("DXGI: acesso perdido, reabrindo");
        try { _dup?.Dispose(); } catch { }
        _dup = null;
        try
        {
            using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            var r = adapter.EnumOutputs(0u, out IDXGIOutput? output);
            if (r.Success && output != null)
            {
                using var o1 = output.QueryInterface<IDXGIOutput1>();
                output.Dispose();
                _dup = o1.DuplicateOutput(_device);
            }
        }
        catch (Exception ex) { Log.Write("DXGI: nao reabriu: " + ex.Message); }
    }

    public void Dispose()
    {
        ReleaseDib();
        try { _staging?.Dispose(); } catch { }
        try { _dup?.Dispose(); } catch { }
        try { _context?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
        _staging = null; _dup = null; _context = null; _device = null;
    }

    [DllImport("gdi32.dll")] private static extern bool BitBlt(
        IntPtr hdcDest, int xd, int yd, int w, int h, IntPtr hdcSrc, int xs, int ys, uint rop);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(
        IntPtr hdcDest, int xd, int yd, int wd, int hd,
        IntPtr hdcSrc, int xs, int ys, int ws, int hs, uint rop);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr p);
}
