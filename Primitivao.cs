using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Primicord;

/// <summary>Identidade e dados de um jogador, vindos do app de apostas.</summary>
public sealed class PrimitivaoUser
{
    public string Nick = "";
    public long Pc;              // Primitivo Coins
    public long Cc;              // Campeao Coins
    public string TeamId = "";
    public string TeamName = "";
    public bool IsAdmin;
    public bool IsMod;

    /// <summary>Hash da senha, guardado local pra re-login silencioso no proximo boot.</summary>
    public string SenhaHash = "";

    public string Badge => IsAdmin ? "ADMIN" : IsMod ? "MOD" : "";

    /// <summary>Id do tema escolhido no site (users[nick].theme).</summary>
    public string ThemeId = "";

    /// <summary>Cor de destaque do tema do site, ou null se nao tem/nao reconhecido.</summary>
    public Color? ThemeAccent => Primitivao.AccentOf(ThemeId);

    /// <summary>
    /// Saldo compacto pro cabecalho. Os saldos reais chegam a 15 digitos
    /// (939.640.313.391.648 PC), que estouraria a largura da barra.
    /// </summary>
    public string PcShort => Compact(Pc);

    public static string Compact(long v)
    {
        if (v < 0) return "-" + Compact(-v);
        (long div, string suf)[] steps =
        {
            (1_000_000_000_000L, "T"), (1_000_000_000L, "B"), (1_000_000L, "M"), (1_000L, "K"),
        };
        foreach (var (div, suf) in steps)
        {
            if (v < div) continue;
            double n = v / (double)div;
            return (n < 10 ? n.ToString("0.0") : n.ToString("0")) + suf;
        }
        return v.ToString();
    }
}

/// <summary>
/// Ponte com o Primitivao: login e dados do jogador.
/// </summary>
/// <remarks>
/// SOMENTE LEITURA. O Primicord nunca escreve em primitivao/apostas — aquele doc e a
/// fonte de verdade da liga (apostas, saldos, campeonatos) e tem landmines conhecidas
/// de perda de dado (CLAUDE.md §2.2). Um app de voz nao tem motivo pra tocar nele.
///
/// Consequencias assumidas de propriedade:
///   - Nao cria conta. Nick que nao existe -> manda criar no site.
///   - Nao migra senha legada (texto puro -> hash). Valida e deixa passar; a migracao
///     acontece no proximo login pelo site, que e quem tem esse direito.
/// </remarks>
public static class Primitivao
{
    private const string AdminNick = "admin";
    private const string AdminPassHash = "969c1c616baed41d32c81907be42da9185cff6193cb6d067c94a32ab933c7ab9";
    private static readonly string[] ModNicks = { "bane", "vitinho", "mohamed" };

    /// <summary>
    /// Cores dos temas do site (espelha a lista de THEMES no apostas-app.jsx).
    /// O jogador escolhe o tema no site; aqui so LEMOS pra pintar o Primicord com a
    /// mesma cor — trocar o tema continua sendo no site, porque isso mora dentro do
    /// doc critico de apostas e este app nunca escreve la.
    /// </summary>
    private static readonly Dictionary<string, Color> ThemeAccents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ametista"] = Color.FromArgb(0x7a, 0x4d, 0xc9),
        ["borgonha"] = Color.FromArgb(0xb5, 0x47, 0x6a),
        ["breu"] = Color.FromArgb(0xd7, 0x64, 0x14),
        ["campo"] = Color.FromArgb(0x2a, 0x8f, 0x3f),
        ["carmesim"] = Color.FromArgb(0xd6, 0x1f, 0x2b),
        ["celin"] = Color.FromArgb(0x3a, 0x86, 0xc8),
        ["chiclete"] = Color.FromArgb(0xc2, 0x18, 0x5b),
        ["classico"] = Color.FromArgb(0xd7, 0x64, 0x14),
        ["floresta"] = Color.FromArgb(0x4a, 0x9d, 0x5a),
        ["grafite"] = Color.FromArgb(0x5a, 0x50, 0x48),
        ["hortela"] = Color.FromArgb(0x1c, 0x8f, 0x86),
        ["musgo"] = Color.FromArgb(0x4f, 0x5a, 0x28),
        ["neon"] = Color.FromArgb(0x1c, 0x8f, 0x86),
        ["oceano"] = Color.FromArgb(0x2f, 0x6f, 0xb0),
        ["orgulho"] = Color.FromArgb(0xd0, 0x2a, 0x9c),
        ["ouro"] = Color.FromArgb(0xc9, 0xa2, 0x27),
        ["petroleo"] = Color.FromArgb(0x1b, 0x49, 0x65),
        ["sangue"] = Color.FromArgb(0xc0, 0x39, 0x2b),
        ["vinho"] = Color.FromArgb(0x8a, 0x28, 0x46),
    };

    public static Color? AccentOf(string? themeId)
        => !string.IsNullOrEmpty(themeId) && ThemeAccents.TryGetValue(themeId, out var c) ? c : null;

    // teamId -> nome de exibicao (espelha TEAMS no apostas-app.jsx)
    private static readonly Dictionary<string, string> TeamNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bane"] = "Bane", ["mohamed"] = "Mohamed", ["potato"] = "Potato", ["magreza"] = "Magreza",
        ["celin"] = "Celin", ["juca"] = "Juca", ["caco"] = "Caco", ["vitinho"] = "Vitinho",
    };

    private static readonly Dictionary<string, Image> AvatarCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object AvatarLock = new();
    private static readonly SemaphoreSlim AvatarLoadGate = new(1, 1);
    private static string _avatarDiskCacheReadFor = "";
    private static readonly TimeSpan AvatarRefreshInterval = TimeSpan.FromHours(24);
    private static string AvatarCachePath => Path.Combine(AppEnv.DataDir, "avatars.json");

    /// <summary>SHA-256 em hex — mesmo formato do hashPassword() do site.</summary>
    public static string HashPassword(string text)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""));
        var sb = new StringBuilder(64);
        foreach (byte b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    public sealed record AuthResult(PrimitivaoUser? User, string? Error,
                                    bool Transient = false, bool QuotaExceeded = false,
                                    bool FromCache = false)
    {
        public bool Ok => User != null;
    }

    /// <summary>Valida nick+senha contra o doc do Primitivao e monta o perfil.</summary>
    public static async Task<AuthResult> AuthenticateAsync(Firestore fs, string nick, string senha,
                                                           CancellationToken ct = default)
        => await AuthCoreAsync(fs, nick, HashPassword(senha), senha, ct).ConfigureAwait(false);

    /// <summary>Re-login silencioso no boot, usando o hash guardado no config.</summary>
    public static async Task<AuthResult> AuthenticateWithHashAsync(Firestore fs, string nick, string hash,
                                                                    CancellationToken ct = default)
        => await AuthCoreAsync(fs, nick, hash, null, ct).ConfigureAwait(false);

    /// <summary>
    /// Usa primeiro a identidade que ja foi validada neste PC. A funcao remota so
    /// e executada quando nick/senha nao correspondem ao perfil salvo, o que evita
    /// gastar uma leitura do Firestore em toda abertura do aplicativo.
    /// </summary>
    public static async Task<AuthResult> AuthenticatePreferCachedAsync(
        Config cfg, string nick, string senhaHash, Func<Task<AuthResult>> authenticateRemote)
    {
        PrimitivaoUser? cached = AuthenticateCached(cfg, nick, senhaHash);
        if (cached != null)
            return new AuthResult(cached, null, FromCache: true);
        return await authenticateRemote().ConfigureAwait(false);
    }

    /// <summary>
    /// Reabre a identidade validada no ultimo login sem uma nova leitura remota.
    /// Nunca aceita outro nick ou outra senha: ambos precisam bater com o config.
    /// </summary>
    public static PrimitivaoUser? AuthenticateCached(Config cfg, string nick, string senhaHash)
    {
        nick = (nick ?? "").Trim().ToLowerInvariant().TrimStart('@');
        if (!string.Equals(nick, cfg.Nick, StringComparison.OrdinalIgnoreCase)) return null;
        if (senhaHash.Length == 0 || !string.Equals(senhaHash, cfg.SenhaHash, StringComparison.Ordinal))
            return null;

        return new PrimitivaoUser
        {
            Nick = nick,
            SenhaHash = senhaHash,
            Pc = cfg.CachedPc,
            Cc = cfg.CachedCc,
            TeamId = cfg.CachedTeamId,
            TeamName = cfg.CachedTeamName,
            ThemeId = cfg.CachedThemeId,
            IsAdmin = nick == AdminNick,
            IsMod = nick == AdminNick || cfg.CachedIsMod
                || ModNicks.Contains(nick, StringComparer.OrdinalIgnoreCase),
        };
    }

    private static async Task<AuthResult> AuthCoreAsync(Firestore fs, string nick, string senhaHash,
                                                         string? senhaPlain, CancellationToken ct)
    {
        nick = (nick ?? "").Trim().ToLowerInvariant().TrimStart('@');
        if (nick.Length == 0) return new AuthResult(null, "Preenche o nick");
        if (senhaHash.Length == 0) return new AuthResult(null, "Preenche a senha");

        // Admin nao vive no mapa de users — hash fixo, igual ao site.
        if (nick == AdminNick)
        {
            if (senhaHash != AdminPassHash) return new AuthResult(null, "Senha de admin incorreta");
            var adm = new PrimitivaoUser { Nick = nick, IsAdmin = true, IsMod = true, SenhaHash = senhaHash };
            await LoadAvatarsAsync(fs, ct).ConfigureAwait(false);
            return new AuthResult(adm, null);
        }

        Dictionary<string, object?>? doc;
        try { doc = await fs.GetAsync("primitivao/apostas", ct).ConfigureAwait(false); }
        catch (FirestoreException ex)
        {
            Log.Write("login: nao li o doc de apostas: " + ex.Message);
            string error = ex.IsQuotaExceeded
                ? "O servidor atingiu o limite de leituras. Tente novamente mais tarde."
                : ex.IsTransient
                    ? "O servidor do Primitivao esta temporariamente indisponivel."
                    : "O Firestore recusou o login: " + ex.Message;
            return new AuthResult(null, error, ex.IsTransient, ex.IsQuotaExceeded);
        }
        catch (HttpRequestException ex)
        {
            Log.Write("login: rede indisponivel: " + ex.Message);
            return new AuthResult(null, "Sem conexao com o servidor. Entrando com o perfil salvo.", true);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            Log.Write("login: servidor demorou demais: " + ex.Message);
            return new AuthResult(null, "O servidor demorou demais para responder.", true);
        }
        if (doc == null) return new AuthResult(null, "O doc do Primitivao nao existe");

        JsonNode? state;
        try { state = JsonNode.Parse(Firestore.Str(doc, "json", "{}")); }
        catch (Exception ex)
        {
            Log.Write("login: json invalido: " + ex.Message);
            return new AuthResult(null, "Dados do Primitivao vieram corrompidos");
        }

        var users = state?["users"] as JsonObject;
        var entry = users?[nick] as JsonObject;
        if (entry == null)
            return new AuthResult(null, $"Nick \"{nick}\" nao existe. Cria a conta no site do Primitivao primeiro.");

        string storedHash = entry["senhaHash"]?.GetValue<string>() ?? "";
        if (storedHash.Length > 0)
        {
            if (storedHash != senhaHash) return new AuthResult(null, "Senha incorreta");
        }
        else
        {
            // Conta legada (senha em texto puro no doc). 7 das 21 contas ainda estao
            // assim. Comparamos hasheando o texto do servidor, o que faz o re-login
            // silencioso funcionar tambem pra elas — senao esses 7 teriam que digitar
            // a senha a cada abertura do app.
            string storedPlain = entry["senha"]?.GetValue<string>() ?? "";
            if (storedPlain.Length == 0)
                return new AuthResult(null, "Conta invalida — fala com o admin");

            bool valid = senhaPlain != null
                ? storedPlain == senhaPlain              // digitou agora
                : HashPassword(storedPlain) == senhaHash; // re-login pelo hash salvo
            if (!valid) return new AuthResult(null, "Senha incorreta");
            // NAO migramos pra hash aqui: escrever nesse doc e trabalho do site.
        }

        var user = new PrimitivaoUser
        {
            Nick = nick,
            Pc = ReadLong(entry["pc"]),
            Cc = ReadLong(entry["cc"]),
            IsMod = ModNicks.Contains(nick, StringComparer.OrdinalIgnoreCase),
            ThemeId = entry["theme"]?.GetValue<string>() ?? "",
            SenhaHash = senhaHash.Length > 0 ? senhaHash : HashPassword(senhaPlain ?? ""),
        };

        // Time da FIFA: teamPlayers e { teamId: nick } dentro do json — invertemos.
        if (state?["teamPlayers"] is JsonObject tp)
        {
            foreach (var (teamId, val) in tp)
            {
                if (!string.Equals(val?.GetValue<string>(), nick, StringComparison.OrdinalIgnoreCase)) continue;
                user.TeamId = teamId;
                user.TeamName = TeamNames.TryGetValue(teamId, out var n) ? n : teamId;
                break;
            }
        }

        await LoadAvatarsAsync(fs, ct).ConfigureAwait(false);
        Log.Write($"login OK: {user.Nick} (pc={user.Pc} cc={user.Cc} time={user.TeamName} badge={user.Badge})");
        return new AuthResult(user, null);
    }

    private static long ReadLong(JsonNode? n)
    {
        if (n == null) return 0;
        try { return n.GetValue<long>(); }
        catch { }
        try { return (long)n.GetValue<double>(); }
        catch { return 0; }
    }

    // ─── ELENCO ──────────────────────────────────────────────────────────────

    private static List<string>? _members;

    /// <summary>
    /// Todos os nicks cadastrados no Primitivao — vira a lista de membros do
    /// Primicord (igual ao Discord: todo mundo aparece, online ou nao).
    /// </summary>
    public static async Task<List<string>> ListMembersAsync(Firestore fs, CancellationToken ct = default,
                                                             bool force = false)
    {
        if (_members is { Count: > 0 } && !force) return _members;
        var list = new List<string>();
        try
        {
            var doc = await fs.GetAsync("primitivao/apostas", ct).ConfigureAwait(false);
            var state = JsonNode.Parse(Firestore.Str(doc ?? new(), "json", "{}"));
            if (state?["users"] is JsonObject users)
                foreach (var (nick, _) in users) list.Add(nick);
            list.Sort(StringComparer.OrdinalIgnoreCase);
        }
        catch (FirestoreException) { throw; }
        catch (Exception ex) { Log.Write("listar membros falhou: " + ex.Message); }
        if (list.Count > 0) _members = list;
        return list;
    }

    // ─── AVATARES ────────────────────────────────────────────────────────────

    /// <summary>
    /// Abre primeiro o cache local e so consulta o doc primitivao/avatars quando
    /// ele nao existe ou passou de 24 horas. Assim o login salvo recupera as fotos
    /// imediatamente e nao cria uma leitura do Firestore em toda abertura.
    /// </summary>
    public static async Task LoadAvatarsAsync(Firestore fs, CancellationToken ct = default, bool force = false)
    {
        LoadCachedAvatars();
        if (!force && AvatarDiskCacheIsFresh()) return;

        await AvatarLoadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Outra chamada pode ter terminado enquanto esta esperava a trava.
            if (!force && AvatarDiskCacheIsFresh()) return;
            var doc = await fs.GetAsync("primitivao/avatars", ct).ConfigureAwait(false);
            if (doc == null) return;
            var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (nick, val) in doc)
                if (val is string dataUrl && dataUrl.Length > 0) data[nick] = dataUrl;

            int loaded = ReplaceAvatarImages(data);
            SaveAvatarDiskCache(data);
            Log.Write($"avatares carregados: {loaded}");
        }
        catch (Exception ex) { Log.Write("avatares falharam: " + ex.Message); }
        finally { AvatarLoadGate.Release(); }
    }

    /// <summary>Carrega as fotos persistidas sem rede. Seguro chamar mais de uma vez.</summary>
    public static int LoadCachedAvatars()
    {
        string path = AvatarCachePath;
        lock (AvatarLock)
        {
            if (string.Equals(_avatarDiskCacheReadFor, path, StringComparison.OrdinalIgnoreCase))
                return AvatarCache.Count;
            _avatarDiskCacheReadFor = path;
            // Importante para o modo portatil e para os harnesses que redirecionam
            // PRIMICORD_DATA_DIR: nunca misturar fotos de duas pastas de dados.
            AvatarCache.Clear();
        }

        try
        {
            if (!File.Exists(path)) return 0;
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(path));
            if (data == null) return 0;
            int loaded = ReplaceAvatarImages(data);
            Log.Write($"avatares locais carregados: {loaded}");
            return loaded;
        }
        catch (Exception ex)
        {
            Log.Write("cache local de avatares falhou: " + ex.Message);
            return 0;
        }
    }

    /// <summary>Foto do jogador, ou null (o tile cai na inicial).</summary>
    public static Image? AvatarFor(string? nick)
    {
        if (string.IsNullOrEmpty(nick)) return null;
        lock (AvatarLock) return AvatarCache.TryGetValue(nick, out var img) ? img : null;
    }

    private static bool AvatarDiskCacheIsFresh()
    {
        try
        {
            var file = new FileInfo(AvatarCachePath);
            lock (AvatarLock)
                if (AvatarCache.Count == 0) return false;
            return file.Exists && file.Length > 2
                && DateTime.UtcNow - file.LastWriteTimeUtc < AvatarRefreshInterval;
        }
        catch { return false; }
    }

    private static int ReplaceAvatarImages(IReadOnlyDictionary<string, string> data)
    {
        var decoded = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        foreach (var (nick, dataUrl) in data)
        {
            var image = DecodeDataUrl(dataUrl);
            if (image != null) decoded[nick] = image;
        }

        lock (AvatarLock)
        {
            // Nao dispose aqui: um paint pode ter acabado de obter a imagem antiga.
            // Ao tirar as referencias, o GC libera os bitmaps depois que o desenho
            // terminar, sem uma corrida de GDI+ durante a atualizacao em background.
            AvatarCache.Clear();
            foreach (var (nick, image) in decoded) AvatarCache[nick] = image;
            return AvatarCache.Count;
        }
    }

    private static void SaveAvatarDiskCache(IReadOnlyDictionary<string, string> data)
    {
        try
        {
            string path = AvatarCachePath;
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data));
            File.Move(temp, path, true);
        }
        catch (Exception ex) { Log.Write("nao salvei cache de avatares: " + ex.Message); }
    }

    private static Image? DecodeDataUrl(string dataUrl)
    {
        try
        {
            int comma = dataUrl.IndexOf(',');
            if (comma < 0 || !dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
            byte[] bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
            // Copia pra um Bitmap proprio: Image.FromStream segura o stream vivo.
            using var ms = new MemoryStream(bytes);
            using var tmp = Image.FromStream(ms);
            return new Bitmap(tmp);
        }
        catch { return null; }
    }
}
