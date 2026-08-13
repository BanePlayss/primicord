using Primicord;

namespace Primicord.Tests;

internal static class Wait
{
    /// <summary>
    /// Espera uma condicao virar verdadeira. Rede nao e sincrona: assertar logo
    /// depois de mandar o pacote da falso-negativo, e dormir um tempo fixo deixa
    /// o teste lento e ainda assim instavel.
    /// </summary>
    public static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs, int pollMs = 25)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(pollMs);
        }
        return condition();
    }
}

/// <summary>
/// Sinalizacao em memoria — o mesmo contrato do Firestore, sem sair do processo.
/// </summary>
/// <remarks>
/// Espelha de proposito a semantica exata do <see cref="WebRtcSignaling"/>,
/// inclusive a parte que importa: publicar oferta LIMPA a resposta anterior. Se
/// este duble mentisse nesse ponto, o teste passaria e a producao quebraria ao
/// renegociar.
/// </remarks>
internal sealed class MemorySignalingBus
{
    private readonly Dictionary<string, SignalDoc> _docs = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public IWebRtcSignaling For(string peerId) => new Endpoint(this, peerId);

    private SignalDoc Get(string key)
    {
        lock (_lock)
        {
            if (!_docs.TryGetValue(key, out var d)) { d = new SignalDoc(); _docs[key] = d; }
            return d;
        }
    }

    private sealed class Endpoint : IWebRtcSignaling
    {
        private readonly MemorySignalingBus _bus;
        private readonly string _me;

        public Endpoint(MemorySignalingBus bus, string me) { _bus = bus; _me = me; }

        public bool IsOfferer(string other) => string.CompareOrdinal(_me, other) < 0;

        private string Key(string other) => WebRtcSignaling.PairKey(_me, other);

        public Task PublishOfferAsync(string other, long epoch, string sdp,
                                      IEnumerable<string> cands, CancellationToken ct = default)
        {
            lock (_bus._lock)
            {
                var d = _bus.Get(Key(other));
                d.Epoch = epoch;
                d.Offer = sdp;
                d.OfferCandidates = string.Join("\n", cands);
                d.OfferAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                d.Answer = "";
                d.AnswerCandidates = "";
                d.AnswerAt = 0;
            }
            return Task.CompletedTask;
        }

        public Task PublishAnswerAsync(string other, string sdp,
                                       IEnumerable<string> cands, CancellationToken ct = default)
        {
            lock (_bus._lock)
            {
                var d = _bus.Get(Key(other));
                d.Answer = sdp;
                d.AnswerCandidates = string.Join("\n", cands);
                d.AnswerAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            return Task.CompletedTask;
        }

        public Task<SignalDoc?> ReadAsync(string other, CancellationToken ct = default)
        {
            lock (_bus._lock)
            {
                if (!_bus._docs.TryGetValue(Key(other), out var d)) return Task.FromResult<SignalDoc?>(null);
                // Copia: o Firestore devolve um retrato, nao uma referencia viva.
                return Task.FromResult<SignalDoc?>(new SignalDoc
                {
                    Epoch = d.Epoch,
                    Offer = d.Offer,
                    OfferCandidates = d.OfferCandidates,
                    OfferAt = d.OfferAt,
                    Answer = d.Answer,
                    AnswerCandidates = d.AnswerCandidates,
                    AnswerAt = d.AnswerAt,
                });
            }
        }

        public Task ClearAsync(string other)
        {
            lock (_bus._lock) _bus._docs.Remove(Key(other));
            return Task.CompletedTask;
        }
    }
}
