using System.Security.Cryptography;
using System.Text;
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

    // teamId -> nome de exibicao (espelha TEAMS no apostas-app.jsx)
    private static readonly Dictionary<string, string> TeamNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bane"] = "Bane", ["mohamed"] = "Mohamed", ["potato"] = "Potato", ["magreza"] = "Magreza",
        ["celin"] = "Celin", ["juca"] = "Juca", ["caco"] = "Caco", ["vitinho"] = "Vitinho",
    };

    private static readonly Dictionary<string, Image> AvatarCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object AvatarLock = new();
    private static bool _avatarsLoaded;

    /// <summary>SHA-256 em hex — mesmo formato do hashPassword() do site.</summary>
    public static string HashPassword(string text)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""));
        var sb = new StringBuilder(64);
        foreach (byte b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    public sealed record AuthResult(PrimitivaoUser? User, string? Error)
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
        catch (Exception ex)
        {
            Log.Write("login: nao li o doc de apostas: " + ex.Message);
            return new AuthResult(null, "Nao consegui falar com o Primitivao. Sem internet?");
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

    // ─── AVATARES ────────────────────────────────────────────────────────────

    /// <summary>
    /// Carrega as fotos dos jogadores (doc primitivao/avatars, { nick: dataUrl }).
    /// Uma vez por sessao — sao as mesmas fotos que aparecem no site.
    /// </summary>
    public static async Task LoadAvatarsAsync(Firestore fs, CancellationToken ct = default, bool force = false)
    {
        if (_avatarsLoaded && !force) return;
        _avatarsLoaded = true;
        try
        {
            var doc = await fs.GetAsync("primitivao/avatars", ct).ConfigureAwait(false);
            if (doc == null) return;
            int loaded = 0;
            foreach (var (nick, val) in doc)
            {
                if (val is not string dataUrl || dataUrl.Length == 0) continue;
                var img = DecodeDataUrl(dataUrl);
                if (img == null) continue;
                lock (AvatarLock)
                {
                    if (AvatarCache.TryGetValue(nick, out var old)) { try { old.Dispose(); } catch { } }
                    AvatarCache[nick] = img;
                }
                loaded++;
            }
            Log.Write($"avatares carregados: {loaded}");
        }
        catch (Exception ex) { Log.Write("avatares falharam: " + ex.Message); }
    }

    /// <summary>Foto do jogador, ou null (o tile cai na inicial).</summary>
    public static Image? AvatarFor(string? nick)
    {
        if (string.IsNullOrEmpty(nick)) return null;
        lock (AvatarLock) return AvatarCache.TryGetValue(nick, out var img) ? img : null;
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
