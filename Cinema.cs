using System.Security.Cryptography;

namespace Primicord;

/// <summary>
/// Sessao cinema: uma pessoa escolhe um arquivo de video, ele e enviado pra todo
/// mundo e a reproducao roda sincronizada.
/// </summary>
/// <remarks>
/// POR QUE MANDAR O ARQUIVO E NAO A TELA: compartilhar tela re-comprime tudo em
/// tempo real e o video sempre sai pior que o original — e justamente video e o
/// pior caso pro nosso compressor (a tela inteira muda a cada quadro). Mandando o
/// ARQUIVO, cada um assiste na qualidade original, sem perda nenhuma, e a rede so
/// trabalha uma vez em vez de 24 vezes por segundo.
///
/// Custo: precisa baixar antes de comecar. Um filme de 2GB em 10 MB/s leva ~3min.
/// Por isso a transferencia mostra progresso e a reproducao so libera quando todo
/// mundo terminou.
///
/// Transporte: chunks pelo mesmo caminho UDP ja furado, com o receptor pedindo de
/// volta (NACK) o que faltou. Nao usamos TCP porque abrir uma segunda conexao
/// exigiria furar o NAT de novo, agora em TCP, que e bem menos confiavel.
/// </remarks>
public sealed class CinemaSession : IDisposable
{
    public const int ChunkPayload = 1000;

    /// <summary>Estado da sessao pra UI.</summary>
    public enum Phase { Ocioso, Enviando, Recebendo, Pronto, Tocando }

    public Phase State { get; private set; } = Phase.Ocioso;
    public string FileName { get; private set; } = "";
    public long FileSize { get; private set; }
    public string? LocalPath { get; private set; }
    public int Percent { get; private set; }
    public string HostNick { get; private set; } = "";

    public event Action? Changed;
    public event Action<string>? Failed;

    private readonly RoomSession _session;
    private readonly string _myNick;

    // envio
    private byte[]? _fileBytes;
    private CancellationTokenSource? _sendCts;

    // recepcao
    private FileStream? _incoming;
    private bool[]? _received;
    private int _chunksTotal, _chunksHave;
    private string _incomingPath = "";
    private uint _hostSender;
    private System.Threading.Timer? _nackTimer;

    public CinemaSession(RoomSession session, string myNick)
    {
        _session = session;
        _myNick = myNick;
        session.CinemaControl += OnControl;
        session.CinemaChunk += OnChunk;
    }

    public void Dispose()
    {
        _session.CinemaControl -= OnControl;
        _session.CinemaChunk -= OnChunk;
        try { _sendCts?.Cancel(); } catch { }
        try { _nackTimer?.Dispose(); } catch { }
        try { _incoming?.Dispose(); } catch { }
        _incoming = null;
        _fileBytes = null;
        State = Phase.Ocioso;
    }

    private void Notify() { try { Changed?.Invoke(); } catch { } }

    // ─── HOST: oferecer o filme ──────────────────────────────────────────────

    /// <summary>Le o arquivo e comeca a mandar pra todo mundo da sala.</summary>
    public async Task StartHostingAsync(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) { Failed?.Invoke("Arquivo nao encontrado."); return; }
            if (fi.Length > 4L * 1024 * 1024 * 1024)
            { Failed?.Invoke("Arquivo maior que 4GB — muito pra mandar pela sala."); return; }

            FileName = fi.Name;
            FileSize = fi.Length;
            LocalPath = path;      // o host ja tem o arquivo
            HostNick = _myNick;
            State = Phase.Enviando;
            Percent = 0;
            Notify();

            _fileBytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            int chunks = (int)((FileSize + ChunkPayload - 1) / ChunkPayload);

            _session.SendCinemaControl(CinemaMsg.Offer(FileName, FileSize, chunks, _myNick));
            Log.Write($"cinema: oferecendo {FileName} ({FileSize / 1024 / 1024} MB, {chunks} pedacos)");

            _sendCts = new CancellationTokenSource();
            _ = Task.Run(() => SendLoop(chunks, _sendCts.Token));
        }
        catch (Exception ex)
        {
            Log.Write("cinema: falha ao abrir: " + ex.Message);
            Failed?.Invoke("Nao consegui abrir o arquivo: " + ex.Message);
            State = Phase.Ocioso;
            Notify();
        }
    }

    /// <summary>
    /// Manda todos os pedacos, depois fica reenviando so os que alguem pediu de volta.
    /// </summary>
    private void SendLoop(int chunks, CancellationToken ct)
    {
        try
        {
            var data = _fileBytes;
            if (data == null) return;

            for (int i = 0; i < chunks && !ct.IsCancellationRequested; i++)
            {
                int off = i * ChunkPayload;
                int len = Math.Min(ChunkPayload, data.Length - off);
                _session.SendCinemaChunk(i, data, off, len);

                // Respiro a cada 32 pedacos: sem isso a rajada estoura o buffer do
                // socket e a maior parte se perde (o mesmo erro que a tela teve).
                if ((i & 31) == 31) Thread.Sleep(1);

                if ((i & 255) == 0)
                {
                    Percent = (int)(i * 100L / Math.Max(1, chunks));
                    Notify();
                }
            }

            Percent = 100;
            State = Phase.Pronto;
            Notify();
            Log.Write("cinema: envio inicial concluido");

            // A partir daqui so responde a pedidos de reenvio (tratados no OnControl).
        }
        catch (Exception ex) { Log.Write("cinema: erro no envio: " + ex.Message); }
    }

    // ─── CONVIDADO: receber ──────────────────────────────────────────────────

    private void BeginReceiving(string name, long size, int chunks, string host, uint hostSender)
    {
        try
        {
            try { _incoming?.Dispose(); } catch { }
            FileName = name;
            FileSize = size;
            HostNick = host;
            _hostSender = hostSender;
            _chunksTotal = chunks;
            _chunksHave = 0;
            _received = new bool[chunks];
            Percent = 0;
            State = Phase.Recebendo;

            string safe = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
            _incomingPath = Path.Combine(AppEnv.CinemaDir, safe);
            _incoming = new FileStream(_incomingPath, FileMode.Create, FileAccess.Write,
                                       FileShare.Read, 1 << 20);
            _incoming.SetLength(size);

            Log.Write($"cinema: recebendo {name} ({size / 1024 / 1024} MB) de {host}");
            Notify();

            // Pede de volta o que faltou, de tempos em tempos.
            _nackTimer?.Dispose();
            _nackTimer = new System.Threading.Timer(_ => RequestMissing(), null, 3000, 2500);
        }
        catch (Exception ex)
        {
            Log.Write("cinema: nao consegui gravar: " + ex.Message);
            Failed?.Invoke("Nao consegui gravar o arquivo: " + ex.Message);
            State = Phase.Ocioso;
            Notify();
        }
    }

    private void OnChunk(uint senderId, int index, byte[] data, int offset, int count)
    {
        var fs = _incoming;
        var got = _received;
        if (fs == null || got == null || index < 0 || index >= got.Length) return;
        if (got[index]) return;

        try
        {
            lock (fs)
            {
                fs.Position = (long)index * ChunkPayload;
                fs.Write(data, offset, count);
            }
            got[index] = true;
            _chunksHave++;

            int pct = (int)(_chunksHave * 100L / Math.Max(1, _chunksTotal));
            if (pct != Percent) { Percent = pct; Notify(); }

            if (_chunksHave == _chunksTotal) FinishReceiving();
        }
        catch (Exception ex) { Log.Write("cinema: erro gravando pedaco: " + ex.Message); }
    }

    private void RequestMissing()
    {
        var got = _received;
        if (got == null || State != Phase.Recebendo) return;

        var missing = new List<int>(256);
        for (int i = 0; i < got.Length && missing.Count < 256; i++)
            if (!got[i]) missing.Add(i);

        if (missing.Count == 0) return;
        _session.SendCinemaControl(CinemaMsg.Nack(missing));
        Log.Write($"cinema: pedindo {missing.Count} pedacos de volta ({_chunksHave}/{_chunksTotal})");
    }

    private void FinishReceiving()
    {
        try
        {
            _nackTimer?.Dispose();
            _nackTimer = null;
            lock (_incoming!) { _incoming.Flush(); _incoming.Dispose(); }
            _incoming = null;
            LocalPath = _incomingPath;
            State = Phase.Pronto;
            Percent = 100;
            Log.Write("cinema: arquivo completo em " + LocalPath);
            Notify();
        }
        catch (Exception ex) { Log.Write("cinema: erro ao fechar: " + ex.Message); }
    }

    // ─── CONTROLE (oferta, nack, play/pause/seek) ────────────────────────────

    public event Action<string, double>? PlaybackCommand;   // ("play"|"pause"|"seek", posicao)

    /// <summary>So o host manda comando; os outros seguem.</summary>
    public void SendPlayback(string action, double positionSeconds)
    {
        if (HostNick != _myNick) return;
        _session.SendCinemaControl(CinemaMsg.Playback(action, positionSeconds));
    }

    private void OnControl(uint senderId, string json)
    {
        try
        {
            var msg = CinemaMsg.Parse(json);
            switch (msg.Kind)
            {
                case "offer":
                    if (msg.Host == _myNick) return;    // e a minha propria oferta
                    BeginReceiving(msg.Name, msg.Size, msg.Chunks, msg.Host, senderId);
                    break;

                case "nack":
                    // Host: alguem perdeu pedacos, reenvia.
                    if (_fileBytes == null) return;
                    foreach (int idx in msg.Missing)
                    {
                        int off = idx * ChunkPayload;
                        if (off >= _fileBytes.Length) continue;
                        int len = Math.Min(ChunkPayload, _fileBytes.Length - off);
                        _session.SendCinemaChunk(idx, _fileBytes, off, len);
                    }
                    Log.Write($"cinema: reenviei {msg.Missing.Count} pedacos");
                    break;

                case "play":
                case "pause":
                case "seek":
                    PlaybackCommand?.Invoke(msg.Kind, msg.Position);
                    break;
            }
        }
        catch (Exception ex) { Log.Write("cinema: controle invalido: " + ex.Message); }
    }

    /// <summary>SHA-256 dos primeiros 4MB — checagem barata de "e o mesmo arquivo".</summary>
    public static string QuickHash(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[Math.Min(4 * 1024 * 1024, fs.Length)];
            fs.ReadExactly(buf);
            return Convert.ToHexString(SHA256.HashData(buf))[..16];
        }
        catch { return ""; }
    }
}

/// <summary>Mensagens de controle do cinema, num JSON minimo feito na mao.</summary>
public static class CinemaMsg
{
    public sealed class Parsed
    {
        public string Kind = "";
        public string Name = "";
        public long Size;
        public int Chunks;
        public string Host = "";
        public double Position;
        public List<int> Missing = new();
    }

    public static string Offer(string name, long size, int chunks, string host)
        => $"{{\"k\":\"offer\",\"n\":\"{Escape(name)}\",\"s\":{size},\"c\":{chunks},\"h\":\"{Escape(host)}\"}}";

    public static string Nack(List<int> missing)
        => $"{{\"k\":\"nack\",\"m\":[{string.Join(",", missing)}]}}";

    public static string Playback(string action, double pos)
        => $"{{\"k\":\"{action}\",\"p\":{pos.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public static Parsed Parse(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var p = new Parsed { Kind = node["k"]?.GetValue<string>() ?? "" };
        p.Name = node["n"]?.GetValue<string>() ?? "";
        p.Host = node["h"]?.GetValue<string>() ?? "";
        try { p.Size = node["s"]?.GetValue<long>() ?? 0; } catch { }
        try { p.Chunks = node["c"]?.GetValue<int>() ?? 0; } catch { }
        try { p.Position = node["p"]?.GetValue<double>() ?? 0; } catch { }
        if (node["m"] is System.Text.Json.Nodes.JsonArray arr)
            foreach (var v in arr) { try { p.Missing.Add(v!.GetValue<int>()); } catch { } }
        return p;
    }
}
