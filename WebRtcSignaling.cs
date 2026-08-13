namespace Primicord;

/// <summary>O que os dois lados trocaram pra montar a conexao (um doc por par).</summary>
public sealed class SignalDoc
{
    /// <summary>
    /// Qual TENTATIVA de negociacao esta valendo (timestamp de quando o ofertante
    /// montou esta oferta). E o que faz o outro lado saber que precisa refazer:
    /// vale tanto pra reentrada na sala quanto pra reoferta depois de silencio.
    /// </summary>
    public long Epoch;

    public string Offer = "";
    public string OfferCandidates = "";
    public long OfferAt;

    public string Answer = "";
    public string AnswerCandidates = "";
    public long AnswerAt;

    public bool HasOffer => Offer.Length > 0;
    public bool HasAnswer => Answer.Length > 0;
}

/// <summary>
/// Troca de SDP e candidatos ICE pelo Firestore — o "ponto de encontro" do WebRTC.
/// </summary>
/// <remarks>
/// POR QUE NAO E TRICKLE ICE: o padrao manda cada candidato assim que ele aparece,
/// pra conexao fechar o mais cedo possivel. Isso pressupoe um canal de sinalizacao
/// instantaneo (websocket). O nosso e o Firestore REST com POLL DE 2 SEGUNDOS —
/// mandar candidato picado nao adianta nada, so multiplica escrita: seriam 4 a 10
/// documentos por par por sessao, e o outro lado so leria tudo junto no proximo
/// poll de qualquer jeito.
///
/// Entao juntamos: espera a coleta de candidatos terminar (ou estourar o timeout),
/// e publica UMA vez o SDP com a lista inteira. Uma escrita por lado, por par.
/// Custa ~1-2s a mais pra conectar e economiza uma classe inteira de corrida.
///
/// LAYOUT: um documento por PAR, com chave ordenada alfabeticamente, entao os dois
/// lados calculam o mesmo caminho sem combinar nada:
///
///   pc_rooms/{sala}/signal/{menorPeerId}__{maiorPeerId}
///
/// Quem oferta e sempre o de peerId menor (CompareOrdinal). Decisao deterministica
/// dos dois lados, sem negociacao — e por isso nao existe "glare" (os dois ofertando
/// ao mesmo tempo), que e o bug classico de sinalizacao em malha.
///
/// PRIVACIDADE: vale o mesmo aviso do <see cref="ChatService"/>. Sem Firebase Auth a
/// rules nao sabe quem esta pedindo, entao estes documentos sao legiveis por quem
/// tiver a chave de API. Um SDP entrega os IPs (locais e publico) de quem esta na
/// sala. Nao piora o que o pc_rooms/peers ja expunha — os mesmos enderecos ja iam
/// pra la em texto puro —, mas nao melhora, e some junto quando a autenticacao entrar.
/// </remarks>
public sealed class WebRtcSignaling
{
    private readonly Firestore _fs;
    private readonly string _roomId;
    private readonly string _myPeerId;

    public WebRtcSignaling(Firestore fs, string roomId, string myPeerId)
    {
        _fs = fs;
        _roomId = roomId;
        _myPeerId = myPeerId;
    }

    /// <summary>true se EU sou quem oferta neste par.</summary>
    public bool IsOfferer(string otherPeerId)
        => string.CompareOrdinal(_myPeerId, otherPeerId) < 0;

    /// <summary>Chave do par, igual dos dois lados.</summary>
    public static string PairKey(string a, string b)
        => string.CompareOrdinal(a, b) <= 0 ? $"{a}__{b}" : $"{b}__{a}";

    private string PathFor(string otherPeerId)
        => $"pc_rooms/{_roomId}/signal/{PairKey(_myPeerId, otherPeerId)}";

    /// <summary>
    /// Publica a oferta. Limpa a resposta velha de proposito: se eu estou ofertando
    /// de novo, qualquer resposta que esteja la e de uma sessao que ja morreu, e
    /// aceita-la faria a conexao nascer apontando pra portas que nao existem mais.
    /// </summary>
    public async Task PublishOfferAsync(string otherPeerId, long epoch, string sdp,
                                        IEnumerable<string> candidates, CancellationToken ct = default)
    {
        await _fs.SetAsync(PathFor(otherPeerId), new Dictionary<string, object?>
        {
            ["epoch"] = epoch,
            ["offer"] = sdp,
            ["offerCand"] = string.Join("\n", candidates),
            ["offerAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["answer"] = "",
            ["answerCand"] = "",
            ["answerAt"] = 0L,
        }, mergeFields: true, ct).ConfigureAwait(false);
        Log.Write($"webrtc: oferta publicada p/ {otherPeerId} (epoch {epoch})");
    }

    /// <summary>Publica a resposta, sem encostar nos campos da oferta.</summary>
    public async Task PublishAnswerAsync(string otherPeerId, string sdp,
                                         IEnumerable<string> candidates, CancellationToken ct = default)
    {
        await _fs.SetAsync(PathFor(otherPeerId), new Dictionary<string, object?>
        {
            ["answer"] = sdp,
            ["answerCand"] = string.Join("\n", candidates),
            ["answerAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }, mergeFields: true, ct).ConfigureAwait(false);
        Log.Write($"webrtc: resposta publicada p/ {otherPeerId}");
    }

    public async Task<SignalDoc?> ReadAsync(string otherPeerId, CancellationToken ct = default)
    {
        var f = await _fs.GetAsync(PathFor(otherPeerId), ct).ConfigureAwait(false);
        if (f is null) return null;
        return new SignalDoc
        {
            Epoch = Firestore.Num(f, "epoch"),
            Offer = Firestore.Str(f, "offer"),
            OfferCandidates = Firestore.Str(f, "offerCand"),
            OfferAt = Firestore.Num(f, "offerAt"),
            Answer = Firestore.Str(f, "answer"),
            AnswerCandidates = Firestore.Str(f, "answerCand"),
            AnswerAt = Firestore.Num(f, "answerAt"),
        };
    }

    /// <summary>Apaga o doc do par (saida limpa, pra nao deixar SDP morto pra tras).</summary>
    public async Task ClearAsync(string otherPeerId)
    {
        try { await _fs.DeleteAsync(PathFor(otherPeerId)).ConfigureAwait(false); }
        catch (Exception ex) { Log.Write($"webrtc: limpar sinalizacao de {otherPeerId}: {ex.Message}"); }
    }

    /// <summary>Quebra a lista de candidatos guardada (uma linha por candidato).</summary>
    public static string[] SplitCandidates(string blob)
        => blob.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
