using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Primicord.Server;

/// <summary>
/// Relay efemero de voz e tela. O servidor nao decodifica nem grava midia: recebe
/// um pacote binario de um participante e o repassa aos demais sockets da sala.
/// Todos os sockets sao conexoes de SAIDA dos clientes, entao Firewall/NAT do PC
/// deixa de ser requisito para ouvir e falar.
/// </summary>
public static class VoiceRelayHub
{
    private const int MaxPacketBytes = 512 * 1024;
    private const int ReceiveBufferBytes = 16 * 1024;
    private static readonly ConcurrentDictionary<string, RoomState> Rooms =
        new(StringComparer.Ordinal);

    public static async Task RunAsync(string roomId, string peerId, uint senderId, string nick,
                                      bool sharedAudio, WebSocket socket, CancellationToken ct)
    {
        RoomState room = Rooms.GetOrAdd(roomId, _ => new RoomState());
        var client = new RelayClient(peerId, senderId, nick, sharedAudio, socket);
        room.Add(client);
        await room.BroadcastPeersAsync().ConfigureAwait(false);

        byte[] buffer = new byte[ReceiveBufferBytes];
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    if (message.Length + result.Count > MaxPacketBytes)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig,
                            "pacote grande demais", CancellationToken.None).ConfigureAwait(false);
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Binary || message.Length == 0)
                    continue;
                await room.BroadcastPacketAsync(client, message.ToArray()).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (WebSocketException) { }
        finally
        {
            room.Remove(client);
            client.Abort();
            await room.BroadcastPeersAsync().ConfigureAwait(false);
        }
    }

    private sealed class RoomState
    {
        private readonly ConcurrentDictionary<string, RelayClient> _clients =
            new(StringComparer.Ordinal);

        public void Add(RelayClient client)
        {
            _clients.AddOrUpdate(client.PeerId, client, (_, old) =>
            {
                old.Abort();
                return client;
            });
        }

        public void Remove(RelayClient client)
        {
            ((ICollection<KeyValuePair<string, RelayClient>>)_clients)
                .Remove(new KeyValuePair<string, RelayClient>(client.PeerId, client));
        }

        public async Task BroadcastPacketAsync(RelayClient sender, byte[] packet)
        {
            RelayClient[] targets = _clients.Values
                .Where(client => !ReferenceEquals(client, sender)).ToArray();
            await Task.WhenAll(targets.Select(client => client.TrySendAsync(
                packet, WebSocketMessageType.Binary, dropIfBusy: true))).ConfigureAwait(false);
        }

        public async Task BroadcastPeersAsync()
        {
            RelayClient[] clients = _clients.Values.ToArray();
            // O mini servidor publicado e trimmed e desliga serializacao por
            // reflection. Montar o JSON com escape explicito evita metadata
            // dinamica e mantem o executavel pequeno.
            RelayClient[] ordered = clients.OrderBy(client => client.SenderId).ToArray();
            string ids = string.Join(',', ordered.Select(client => client.SenderId));
            string members = string.Join(',', ordered.Select(client =>
                "{\"id\":" + client.SenderId + ",\"nick\":\""
                + JsonEncodedText.Encode(client.Nick).ToString()
                + "\",\"sharedAudio\":" + (client.SharedAudio ? "true" : "false") + "}"));
            byte[] snapshot = Encoding.UTF8.GetBytes(
                "{\"type\":\"peers\",\"screen\":true,\"sharedAudio\":true,\"ids\":[" + ids
                + "],\"members\":[" + members + "]}");
            await Task.WhenAll(clients.Select(client => client.TrySendAsync(
                snapshot, WebSocketMessageType.Text, dropIfBusy: false))).ConfigureAwait(false);
        }
    }

    private sealed class RelayClient(string peerId, uint senderId, string nick,
                                     bool sharedAudio, WebSocket socket)
    {
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        public string PeerId { get; } = peerId;
        public uint SenderId { get; } = senderId;
        public string Nick { get; } = nick;
        public bool SharedAudio { get; } = sharedAudio;

        public async Task TrySendAsync(byte[] payload, WebSocketMessageType type, bool dropIfBusy)
        {
            // Voz velha e pior que um frame perdido. Se este socket ainda estiver
            // enviando o pacote anterior, descarta em vez de criar latencia infinita.
            if (dropIfBusy)
            {
                if (!await _sendGate.WaitAsync(0).ConfigureAwait(false)) return;
            }
            else
            {
                await _sendGate.WaitAsync().ConfigureAwait(false);
            }
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.SendAsync(payload, type, true, timeout.Token).ConfigureAwait(false);
                }
            }
            catch { }
            finally { _sendGate.Release(); }
        }

        public void Abort() { try { socket.Abort(); } catch { } }
    }
}
