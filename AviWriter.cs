using System.Text;

namespace Primicord;

/// <summary>
/// Escreve um AVI com video MJPEG + audio PCM.
/// </summary>
/// <remarks>
/// POR QUE AVI E NAO MP4: os quadros ja chegam prontos em JPEG e o audio em PCM, e
/// AVI aceita os dois crus — da pra montar o arquivo so posicionando bytes, sem
/// encoder, sem DLL nativa, sem FFmpeg junto do exe. MP4 exigiria um muxer H.264/AAC.
/// Abre no VLC, no Media Player e sobe no Discord normal.
///
/// Estrutura: RIFF('AVI ' LIST('hdrl' avih + strl(video) + strl(audio))
///                        LIST('movi' 00dc/01wb intercalados) idx1)
/// Os tamanhos so sao conhecidos no fim, entao ficam zerados e sao preenchidos
/// no Finish() voltando com Seek.
/// </remarks>
public sealed class AviWriter : IDisposable
{
    private readonly FileStream _fs;
    private readonly BinaryWriter _w;
    private readonly int _width, _height, _fps;
    private readonly int _sampleRate, _channels;

    private long _riffSizePos, _moviListSizePos, _moviDataStart;
    private long _avihTotalFramesPos, _vidLengthPos, _audLengthPos, _maxBytesPos;
    private int _videoFrames, _audioBytes, _maxFrameBytes;

    private readonly List<(uint Ckid, uint Flags, uint Offset, uint Length)> _index = new();

    private const uint AvifHasIndex = 0x00000010;
    private const uint AviifKeyframe = 0x00000010;

    public AviWriter(string path, int width, int height, int fps, int sampleRate = 48000, int channels = 1)
    {
        _width = width; _height = height; _fps = Math.Max(1, fps);
        _sampleRate = sampleRate; _channels = channels;

        _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        _w = new BinaryWriter(_fs);
        WriteHeaders();
    }

    private static uint FourCC(string s) =>
        (uint)(s[0] | (s[1] << 8) | (s[2] << 16) | (s[3] << 24));

    private void Tag(string s) => _w.Write(Encoding.ASCII.GetBytes(s));

    private void WriteHeaders()
    {
        Tag("RIFF");
        _riffSizePos = _fs.Position;
        _w.Write(0);                      // tamanho total (preenchido no fim)
        Tag("AVI ");

        // ── LIST hdrl ──
        Tag("LIST");
        long hdrlSizePos = _fs.Position;
        _w.Write(0);
        Tag("hdrl");

        // avih (MainAVIHeader)
        Tag("avih");
        _w.Write(56);
        _w.Write(1_000_000 / _fps);       // microsegundos por quadro
        _maxBytesPos = _fs.Position;
        _w.Write(0);                      // dwMaxBytesPerSec (preenchido no fim)
        _w.Write(0);                      // padding
        _w.Write(AvifHasIndex);
        _avihTotalFramesPos = _fs.Position;
        _w.Write(0);                      // dwTotalFrames
        _w.Write(0);                      // dwInitialFrames
        _w.Write(2);                      // dwStreams (video + audio)
        _w.Write(0);                      // dwSuggestedBufferSize
        _w.Write(_width);
        _w.Write(_height);
        for (int i = 0; i < 4; i++) _w.Write(0);

        // ── strl do video ──
        Tag("LIST");
        long vidListPos = _fs.Position;
        _w.Write(0);
        Tag("strl");

        Tag("strh");
        _w.Write(56);
        _w.Write(FourCC("vids"));
        _w.Write(FourCC("MJPG"));
        _w.Write(0);                      // flags
        _w.Write((short)0);               // priority
        _w.Write((short)0);               // language
        _w.Write(0);                      // initial frames
        _w.Write(1);                      // dwScale
        _w.Write(_fps);                   // dwRate -> fps = rate/scale
        _w.Write(0);                      // dwStart
        _vidLengthPos = _fs.Position;
        _w.Write(0);                      // dwLength (nº de quadros)
        _w.Write(0);                      // suggested buffer
        _w.Write(-1);                     // quality (padrao)
        _w.Write(0);                      // sample size (0 = variavel)
        _w.Write((short)0); _w.Write((short)0);
        _w.Write((short)_width); _w.Write((short)_height);

        Tag("strf");
        _w.Write(40);                     // BITMAPINFOHEADER
        _w.Write(40);
        _w.Write(_width);
        _w.Write(_height);
        _w.Write((short)1);               // planes
        _w.Write((short)24);              // bpp
        _w.Write(FourCC("MJPG"));
        _w.Write(_width * _height * 3);   // tamanho da imagem
        _w.Write(0); _w.Write(0); _w.Write(0); _w.Write(0);

        PatchSize(vidListPos);

        // ── strl do audio ──
        Tag("LIST");
        long audListPos = _fs.Position;
        _w.Write(0);
        Tag("strl");

        int blockAlign = 2 * _channels;
        Tag("strh");
        _w.Write(56);
        _w.Write(FourCC("auds"));
        _w.Write(0);                      // handler (PCM)
        _w.Write(0);
        _w.Write((short)0); _w.Write((short)0);
        _w.Write(0);
        _w.Write(blockAlign);             // dwScale = block align
        _w.Write(_sampleRate * blockAlign); // dwRate = bytes por segundo
        _w.Write(0);
        _audLengthPos = _fs.Position;
        _w.Write(0);                      // dwLength (em blocos)
        _w.Write(0);
        _w.Write(-1);
        _w.Write(blockAlign);             // sample size
        _w.Write((short)0); _w.Write((short)0); _w.Write((short)0); _w.Write((short)0);

        Tag("strf");
        _w.Write(16);                     // WAVEFORMATEX sem cbSize
        _w.Write((short)1);               // WAVE_FORMAT_PCM
        _w.Write((short)_channels);
        _w.Write(_sampleRate);
        _w.Write(_sampleRate * blockAlign);
        _w.Write((short)blockAlign);
        _w.Write((short)16);              // bits por amostra

        PatchSize(audListPos);
        PatchSize(hdrlSizePos);

        // ── LIST movi ──
        Tag("LIST");
        _moviListSizePos = _fs.Position;
        _w.Write(0);
        _moviDataStart = _fs.Position;    // aponta pro fourcc 'movi'
        Tag("movi");
    }

    /// <summary>Fecha um LIST/chunk preenchendo o tamanho que ficou pendente.</summary>
    private void PatchSize(long sizePos)
    {
        long end = _fs.Position;
        _fs.Position = sizePos;
        _w.Write((int)(end - sizePos - 4));
        _fs.Position = end;
    }

    public void WriteVideoFrame(byte[] jpeg, int length)
    {
        long chunkPos = _fs.Position;
        Tag("00dc");
        _w.Write(length);
        _w.Write(jpeg, 0, length);
        if ((length & 1) != 0) _w.Write((byte)0);   // chunks sao alinhados em 2 bytes

        _index.Add((FourCC("00dc"), AviifKeyframe, (uint)(chunkPos - _moviDataStart), (uint)length));
        _videoFrames++;
        if (length > _maxFrameBytes) _maxFrameBytes = length;
    }

    public void WriteAudio(byte[] pcm, int offset, int length)
    {
        if (length <= 0) return;
        long chunkPos = _fs.Position;
        Tag("01wb");
        _w.Write(length);
        _w.Write(pcm, offset, length);
        if ((length & 1) != 0) _w.Write((byte)0);

        _index.Add((FourCC("01wb"), 0, (uint)(chunkPos - _moviDataStart), (uint)length));
        _audioBytes += length;
    }

    public void Finish()
    {
        PatchSize(_moviListSizePos);

        // idx1 — o indice que deixa o player buscar no arquivo.
        Tag("idx1");
        _w.Write(_index.Count * 16);
        foreach (var (ckid, flags, offset, len) in _index)
        {
            _w.Write(ckid);
            _w.Write(flags);
            _w.Write(offset);
            _w.Write(len);
        }

        // Volta e preenche os campos que so agora sao conhecidos.
        long end = _fs.Position;

        _fs.Position = _riffSizePos; _w.Write((int)(end - _riffSizePos - 4));
        _fs.Position = _avihTotalFramesPos; _w.Write(_videoFrames);
        _fs.Position = _vidLengthPos; _w.Write(_videoFrames);
        _fs.Position = _audLengthPos; _w.Write(_audioBytes / (2 * _channels));

        int seconds = Math.Max(1, _videoFrames / _fps);
        _fs.Position = _maxBytesPos;
        _w.Write((int)((_maxFrameBytes * (long)_fps) + _sampleRate * 2L * _channels));

        _fs.Position = end;
        _w.Flush();
        Log.Write($"clipe: {_videoFrames} quadros, {_audioBytes / 1024} KB de audio, ~{seconds}s");
    }

    public void Dispose()
    {
        try { _w.Dispose(); } catch { }
        try { _fs.Dispose(); } catch { }
    }
}
