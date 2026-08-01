using System.Text;

namespace Primicord;

public sealed class RoomInfo
{
    public string Id = "";
    public string Name = "";
    public string CreatedBy = "";
    public readonly List<string> Occupants = new();
    public int Count => Occupants.Count;
}

/// <summary>Lista/cria/apaga salas no Firestore — o "lobby".</summary>
public sealed class RoomDirectory
{
    private const int PeerStaleMs = 15000;
    private readonly Firestore _fs;

    public RoomDirectory(Firestore fs) => _fs = fs;

    public async Task<List<RoomInfo>> ListAsync(CancellationToken ct = default)
    {
        var rooms = new List<RoomInfo>();
        var docs = await _fs.ListAsync("pc_rooms", ct: ct).ConfigureAwait(false);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var (id, f) in docs)
        {
            var room = new RoomInfo
            {
                Id = id,
                Name = Firestore.Str(f, "name", id),
                CreatedBy = Firestore.Str(f, "createdBy"),
            };

            // Quem esta dentro AGORA (heartbeat fresco). Presenca velha e sessao que
            // morreu sem despedida — fecharam o app no botao X, caiu a luz, etc.
            try
            {
                var peers = await _fs.ListAsync($"pc_rooms/{id}/peers", ct: ct).ConfigureAwait(false);
                foreach (var (_, pf) in peers)
                    if (now - Firestore.Num(pf, "lastSeen") <= PeerStaleMs)
                        room.Occupants.Add(Firestore.Str(pf, "nick", "?"));
            }
            catch (Exception ex) { Log.Write($"peers da sala {id}: " + ex.Message); }

            rooms.Add(room);
        }
        return rooms;
    }

    public async Task<RoomInfo> CreateAsync(string name, string createdBy, CancellationToken ct = default)
    {
        string id = Slug(name) + "-" + Random.Shared.Next(0x1000, 0xFFFF).ToString("x4");
        var fields = new Dictionary<string, object?>
        {
            ["name"] = name.ToUpperInvariant(),
            ["createdBy"] = createdBy,
            ["createdAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        await _fs.SetAsync($"pc_rooms/{id}", fields, ct: ct).ConfigureAwait(false);
        Log.Write($"sala criada: {id} ({name})");
        return new RoomInfo { Id = id, Name = name.ToUpperInvariant(), CreatedBy = createdBy };
    }

    /// <summary>Apaga a sala e os restos de presenca. So faz sentido com a sala vazia.</summary>
    public async Task DeleteAsync(string roomId, CancellationToken ct = default)
    {
        try
        {
            var peers = await _fs.ListAsync($"pc_rooms/{roomId}/peers", ct: ct).ConfigureAwait(false);
            foreach (var (pid, _) in peers)
                await _fs.DeleteAsync($"pc_rooms/{roomId}/peers/{pid}", ct).ConfigureAwait(false);
        }
        catch (Exception ex) { Log.Write("limpar peers falhou: " + ex.Message); }

        await _fs.DeleteAsync($"pc_rooms/{roomId}", ct).ConfigureAwait(false);
        Log.Write($"sala apagada: {roomId}");
    }

    private static string Slug(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            if (sb.Length >= 24) break;
        }
        string outp = sb.ToString().Trim('-');
        return outp.Length == 0 ? "sala" : outp;
    }
}

/// <summary>Preferencias locais (nick, dispositivos) em %APPDATA%\Primicord\config.txt.</summary>
public sealed class Config
{
    public string Nick = "";
    /// <summary>
    /// Hash da senha do Primitivao, pra nao pedir login todo boot. Nao e um segredo
    /// novo: o doc primitivao/apostas e de leitura publica e ja expoe esses mesmos
    /// hashes pra qualquer um — guardar aqui nao aumenta a exposicao.
    /// </summary>
    public string SenhaHash = "";
    public int MicDevice = 0;
    public string OutputDeviceId = "";
    public bool PushToTalk;

    /// <summary>Atalho global do clipe (padrao F9), serializado como "mods:tecla".</summary>
    public string ClipHotkey = HotkeyBinding.Serialize(HotkeyBinding.Mods.None, Keys.F9);

    /// <summary>Quantos segundos o clipe guarda pra tras.</summary>
    public int ClipSeconds = 30;

    /// <summary>
    /// Manter o buffer rolando sozinho enquanto alguem compartilha tela. Sem isso o
    /// atalho falharia calado — quando a coisa engracada acontece ja e tarde pra ligar.
    /// Fica so na memoria e e descartado continuamente; so vira arquivo no atalho.
    /// </summary>
    public bool AutoBuffer = true;

    /// <summary>
    /// Teto de subida do compartilhamento de tela, em KB/s (total, dividido entre
    /// os espectadores). Depois que a captura passou a ser pela GPU, ESTE virou o
    /// unico limite da qualidade — nao ha mais gargalo de CPU pra contornar.
    /// </summary>
    public int ScreenBudgetKb = 2000;

    /// <summary>Usar o tema (cor) que o jogador escolheu no site do Primitivao.</summary>
    public bool UseSiteTheme = true;

    /// <summary>Volume da musica do DJ (0..200%), separado do volume das vozes.</summary>
    public int MusicVolume = 70;

    /// <summary>Minimizar pra bandeja em vez de fechar.</summary>
    public bool TrayOnClose = true;

    /// <summary>Bipe quando alguem entra ou sai da sala de voz.</summary>
    public bool JoinLeaveSound = true;

    private static string Path_ => System.IO.Path.Combine(AppEnv.DataDir, "config.txt");

    public static Config Load()
    {
        var c = new Config();
        try
        {
            if (!File.Exists(Path_)) return c;
            foreach (string line in File.ReadAllLines(Path_))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line[..eq].Trim(), v = line[(eq + 1)..].Trim();
                switch (k)
                {
                    case "nick": c.Nick = v; break;
                    case "hash": c.SenhaHash = v; break;
                    case "mic": if (int.TryParse(v, out var m)) c.MicDevice = m; break;
                    case "out": c.OutputDeviceId = v; break;
                    case "ptt": c.PushToTalk = v == "1"; break;
                    case "cliphotkey": c.ClipHotkey = v; break;
                    case "clipsecs": if (int.TryParse(v, out var cs)) c.ClipSeconds = Math.Clamp(cs, 5, 60); break;
                    case "autobuf": c.AutoBuffer = v != "0"; break;
                    case "scrkb": if (int.TryParse(v, out var sk)) c.ScreenBudgetKb = Math.Clamp(sk, 200, 6000); break;
                    case "sitetheme": c.UseSiteTheme = v != "0"; break;
                    case "musicvol": if (int.TryParse(v, out var mv)) c.MusicVolume = Math.Clamp(mv, 0, 200); break;
                    case "tray": c.TrayOnClose = v != "0"; break;
                    case "joinsound": c.JoinLeaveSound = v != "0"; break;
                }
            }
        }
        catch (Exception ex) { Log.Write("config nao carregou: " + ex.Message); }
        return c;
    }

    public void Save()
    {
        try
        {
            File.WriteAllLines(Path_, new[]
            {
                "nick=" + Nick,
                "hash=" + SenhaHash,
                "mic=" + MicDevice,
                "out=" + OutputDeviceId,
                "ptt=" + (PushToTalk ? "1" : "0"),
                "cliphotkey=" + ClipHotkey,
                "clipsecs=" + ClipSeconds,
                "autobuf=" + (AutoBuffer ? "1" : "0"),
                "scrkb=" + ScreenBudgetKb,
                "sitetheme=" + (UseSiteTheme ? "1" : "0"),
                "musicvol=" + MusicVolume,
                "tray=" + (TrayOnClose ? "1" : "0"),
                "joinsound=" + (JoinLeaveSound ? "1" : "0"),
            });
        }
        catch (Exception ex) { Log.Write("config nao salvou: " + ex.Message); }
    }
}
