using System.Drawing.Imaging;
using FlashCap;

namespace Primicord;

/// <summary>Uma camera que o Windows enxerga.</summary>
public sealed record WebcamDevice(string Name, object Descriptor)
{
    public override string ToString() => Name;
}

/// <summary>
/// Camera: pega os quadros e entrega em JPEG, prontos pra rede.
/// </summary>
/// <remarks>
/// POR QUE FLASHCAP E NAO MEDIA FOUNDATION / WINRT: e P/Invoke puro, sem binario
/// nativo junto — nao engorda o exe (que ja tem 101MB) e nao entra no risco de
/// empacotamento que o libVLC nos deu. Media Foundation por COM daria o mesmo
/// resultado com muito mais cerimonia.
///
/// O ATALHO QUE VALE OURO: webcam USB ja comprime em MJPEG dentro dela. Medido na
/// C270 desta maquina, pedindo o formato JPEG:
///
///   320x240 -> 2,6 KB por quadro
///   640x480 -> 7,3 KB por quadro
///
/// Ou seja, os quadros JA CHEGAM em JPEG e vao pra rede sem passar por
/// compressao nenhuma aqui. Zero CPU de encode, ao contrario da tela, que
/// comprime cada bloco a cada quadro. Quando a camera so oferece formato cru
/// (YUYV), ai sim convertemos — e o custo aparece.
///
/// A CONTA DA MALHA: cada espectador recebe uma copia, entao a subida multiplica
/// por N-1. Medido de ponta a ponta nesta maquina, com teto de 15 fps:
///
///   320x240 -> ~200 kbps por espectador  (sala de 6: ~1,0 Mbps de subida)
///   640x480 -> ~570 kbps por espectador  (sala de 6: ~2,9 Mbps)
///
/// Por isso o padrao e 320x240, e nao a resolucao maxima que a camera aceita.
///
/// SOBRE A TAXA REAL: webcam barata derruba o fps sozinha com pouca luz — a C270
/// entregou ~9,8 fps pedindo 30, num quarto normal. Nao adianta pedir mais: o
/// limite e o tempo de exposicao do sensor, nao o codigo.
/// </remarks>
public sealed class WebcamCapture : IDisposable
{
    private readonly CaptureDevices _devices = new();
    private CaptureDevice? _device;
    private volatile bool _running;

    private readonly int _minFrameMs;

    /// <summary>Instante em que o proximo quadro pode passar (ver <see cref="NaHora"/>).</summary>
    private long _nextDue;

    /// <summary>Quadro pronto pra rede: (jpeg, largura, altura). Vem da thread da camera.</summary>
    public event Action<byte[], int, int>? FrameReady;

    /// <summary>Erro que o usuario precisa ver.</summary>
    public event Action<string>? Failed;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool NativeJpeg { get; private set; }
    public int FramesSent { get; private set; }
    public int KbPerSecond { get; private set; }

    private long _statTicks;
    private int _bytesWindow, _framesWindow;

    public WebcamCapture(int maxFps = 15)
        => _minFrameMs = Math.Max(1, 1000 / Math.Clamp(maxFps, 1, 30));

    /// <summary>Cameras disponiveis. Lista vazia = nenhuma, ou todas ocupadas.</summary>
    public static List<WebcamDevice> List()
    {
        var list = new List<WebcamDevice>();
        try
        {
            foreach (var d in new CaptureDevices().EnumerateDescriptors())
            {
                // Sem nenhum formato utilizavel nao adianta oferecer na lista.
                if (d.Characteristics.Length == 0) continue;
                list.Add(new WebcamDevice(d.Name, d));
            }
        }
        catch (Exception ex) { Log.Write("camera: enumerar falhou: " + ex.Message); }
        return list;
    }

    /// <summary>
    /// Liga a camera. <paramref name="deviceName"/> vazio = a primeira que aceitar.
    /// </summary>
    public async Task<bool> StartAsync(string deviceName, int width, int height)
    {
        if (_running) return true;

        try
        {
            var descriptors = _devices.EnumerateDescriptors()
                                      .Where(d => d.Characteristics.Length > 0).ToList();
            if (descriptors.Count == 0)
            {
                Failed?.Invoke("Nao encontrei nenhuma camera.");
                return false;
            }

            var chosen = descriptors.FirstOrDefault(
                             d => d.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
                         ?? descriptors.OrderByDescending(
                             d => d.Characteristics.Count(c => c.PixelFormat == PixelFormats.JPEG))
                            .First();

            var characteristics = PickFormat(chosen, width, height);
            if (characteristics == null)
            {
                Failed?.Invoke($"A camera {chosen.Name} nao tem nenhum formato utilizavel.");
                return false;
            }

            Width = characteristics.Width;
            Height = characteristics.Height;
            NativeJpeg = characteristics.PixelFormat == PixelFormats.JPEG;

            _device = await chosen.OpenAsync(characteristics, OnFrameAsync).ConfigureAwait(false);
            await _device.StartAsync().ConfigureAwait(false);
            _running = true;

            Log.Write($"camera: {chosen.Name} em {Width}x{Height} "
                    + (NativeJpeg ? "JPEG nativo (sem re-encode)" : "formato cru (convertendo)"));
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("camera: nao abriu: " + ex);
            Failed?.Invoke("Nao consegui abrir a camera: " + ex.Message);
            Dispose();
            return false;
        }
    }

    /// <summary>
    /// Escolhe o formato: o mais proximo do tamanho pedido, preferindo JPEG.
    /// </summary>
    /// <remarks>
    /// Preferir JPEG nao e detalhe de performance — e a diferenca entre encaminhar
    /// os bytes e recomprimir 15 vezes por segundo. Se nao houver JPEG no tamanho
    /// pedido, um JPEG de OUTRO tamanho ainda vale mais que um cru do tamanho certo.
    /// </remarks>
    private static VideoCharacteristics? PickFormat(CaptureDeviceDescriptor d, int w, int h)
    {
        static long Distance(VideoCharacteristics c, int w, int h)
            => (long)Math.Abs(c.Width - w) * Math.Abs(c.Width - w)
             + (long)Math.Abs(c.Height - h) * Math.Abs(c.Height - h);

        return d.Characteristics
                .OrderByDescending(c => c.PixelFormat == PixelFormats.JPEG)
                .ThenBy(c => Distance(c, w, h))
                .ThenByDescending(c => c.FramesPerSecond)
                .FirstOrDefault();
    }

    private async Task OnFrameAsync(PixelBufferScope scope)
    {
        if (!_running) { await Task.CompletedTask; return; }

        try
        {
            // Descarta o excedente ANTES de tocar nos bytes: se a camera entrega 30
            // e queremos 15, nao ha por que converter (ou sequer copiar) o resto.
            long now = Environment.TickCount64;
            if (!NaHora(now)) { await Task.CompletedTask; return; }

            byte[] image = scope.Buffer.ExtractImage();
            byte[] jpeg = NativeJpeg ? image : ToJpeg(image);
            if (jpeg.Length == 0) { await Task.CompletedTask; return; }

            FramesSent++;
            _bytesWindow += jpeg.Length;
            _framesWindow++;
            if (now - _statTicks >= 1000)
            {
                KbPerSecond = _bytesWindow / 1024;
                _bytesWindow = 0; _framesWindow = 0; _statTicks = now;
            }

            FrameReady?.Invoke(jpeg, Width, Height);
        }
        catch (Exception ex) { Log.Write("camera: quadro falhou: " + ex.Message); }
        await Task.CompletedTask;
    }

    /// <summary>
    /// Decide se este quadro passa, respeitando o teto de fps.
    /// </summary>
    /// <remarks>
    /// PRAZO QUE ANDA, e nao "faz quanto tempo desde o ultimo". A versao ingenua
    /// ("descarta se faz menos de 66ms") parece certa e derruba o dobro do que
    /// deveria, porque a camera nao entrega em intervalos exatos: com a fonte a
    /// 9,8 fps (~102ms) e teto de 15 (66ms), um quadro que chegasse 60ms depois era
    /// descartado e o seguinte so vinha 160ms adiante. Medido: 9,8 fps viravam 5,6.
    ///
    /// Aqui o prazo AVANCA uma janela por quadro aceito e se realinha quando fica
    /// pra tras, entao fonte mais lenta que o teto passa inteira — que e o caso
    /// normal de webcam barata em ambiente sem muita luz.
    /// </remarks>
    private bool NaHora(long now)
    {
        if (_nextDue == 0) _nextDue = now;
        if (now < _nextDue) return false;
        _nextDue += _minFrameMs;
        if (_nextDue < now) _nextDue = now;   // ficou pra tras: realinha em vez de acumular divida
        return true;
    }

    /// <summary>Converte um quadro cru (DIB) em JPEG. So roda em camera sem MJPEG.</summary>
    private static byte[] ToJpeg(byte[] raw)
    {
        try
        {
            using var src = new MemoryStream(raw, writable: false);
            using var img = Image.FromStream(src);
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(Encoder.Quality, 60L);
            using var outMs = new MemoryStream(64 * 1024);
            img.Save(outMs, codec, ep);
            return outMs.ToArray();
        }
        catch (Exception ex)
        {
            Log.Write("camera: conversao pra JPEG falhou: " + ex.Message);
            return Array.Empty<byte>();
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _device?.StopAsync().Wait(1500); } catch { }
        try { _device?.DisposeAsync().Wait(1500); } catch { }
        _device = null;
        Log.Write("camera: desligada");
    }
}

/// <summary>
/// As cameras que estao chegando, uma imagem viva por pessoa.
/// </summary>
/// <remarks>
/// Cada pessoa tem UM bitmap que dura a sessao inteira e e repintado a cada quadro,
/// em vez de nascer um bitmap novo 15 vezes por segundo. Com quadro novo a cada vez,
/// seriam ~15 bitmaps por segundo por pessoa indo pro coletor, cada um segurando
/// memoria nao-gerenciada do GDI ate o finalizador rodar — pressao que nao precisa
/// existir.
///
/// A tela e desenhada no tamanho do tile, nao no da camera: a reducao acontece UMA
/// vez por quadro aqui, e nao a cada repintura da UI.
///
/// Mesma escolha do <see cref="ScreenReceiver"/> quanto a corrida: a UI le a imagem
/// enquanto a thread de rede pode estar repintando. O lock cobre as duas pontas, e
/// o que ele protege e um DrawImage de imagem pequena — microssegundos.
/// </remarks>
public sealed class WebcamWall : IDisposable
{
    /// <summary>Sem quadro novo por este tempo, a pessoa desligou a camera.</summary>
    private const int StaleMs = 3000;

    private sealed class Slot
    {
        public Bitmap Bmp = null!;
        public Graphics G = null!;
        public long LastTicks;
    }

    private readonly int _w, _h;
    private readonly object _lock = new();
    private readonly Dictionary<uint, Slot> _slots = new();

    public WebcamWall(int width = 186, int height = 124) { _w = width; _h = height; }

    /// <summary>Chega um quadro (thread de rede). Devolve false se veio ilegivel.</summary>
    public bool OnFrame(uint senderId, byte[] jpeg)
    {
        try
        {
            using var ms = new MemoryStream(jpeg, writable: false);
            using var img = Image.FromStream(ms);

            lock (_lock)
            {
                if (!_slots.TryGetValue(senderId, out var slot))
                {
                    var bmp = new Bitmap(_w, _h, PixelFormat.Format24bppRgb);
                    slot = new Slot { Bmp = bmp, G = Graphics.FromImage(bmp) };
                    slot.G.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                    _slots[senderId] = slot;
                }
                slot.G.DrawImage(img, 0, 0, _w, _h);
                slot.LastTicks = DateTime.UtcNow.Ticks;
            }
            return true;
        }
        catch
        {
            // JPEG truncado por pedaco perdido: o proximo quadro conserta.
            return false;
        }
    }

    /// <summary>A camera de alguem, ou null se ela parou de chegar.</summary>
    public Image? FrameOf(uint senderId)
    {
        lock (_lock)
        {
            if (!_slots.TryGetValue(senderId, out var s)) return null;
            if (DateTime.UtcNow.Ticks - s.LastTicks > TimeSpan.TicksPerMillisecond * StaleMs) return null;
            return s.Bmp;
        }
    }

    public void Remove(uint senderId)
    {
        lock (_lock)
        {
            if (!_slots.TryGetValue(senderId, out var s)) return;
            try { s.G.Dispose(); s.Bmp.Dispose(); } catch { }
            _slots.Remove(senderId);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var s in _slots.Values) { try { s.G.Dispose(); s.Bmp.Dispose(); } catch { } }
            _slots.Clear();
        }
    }
}
