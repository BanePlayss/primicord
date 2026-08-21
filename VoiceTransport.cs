namespace Primicord;

/// <summary>
/// Por onde a voz viaja. Duas implementacoes de verdade: a malha UDP propria
/// (<see cref="RoomSession"/>) e o WebRTC (<see cref="WebRtcVoiceMesh"/>).
/// </summary>
/// <remarks>
/// Esta interface existe porque ha DUAS implementacoes, nao porque interface e
/// bonito. Enquanto o WebRTC nao provar que aguenta o tranco, a malha antiga
/// continua sendo o caminho testado — e trocar entre as duas precisa ser uma
/// linha no config, nao um branch.
///
/// O <see cref="VoiceEngine"/> conversa SO com isto: ele entrega frames de 10ms
/// do microfone e recebe PCM de volta identificado por senderId. Quem carrega
/// esses bytes (UDP cru ou RTP/SRTP com Opus) ele nao sabe e nao precisa saber.
///
/// IMPORTANTE — o senderId: e sempre <see cref="RoomSession.HashId"/> do peerId.
/// A UI indexa tiles, volumes e medidores de nivel por esse numero, entao as duas
/// implementacoes TEM que produzir o mesmo id pra mesma pessoa.
/// </remarks>
public interface IVoiceTransport
{
    /// <summary>Enquanto true, <see cref="SendVoice"/> descarta em vez de mandar.</summary>
    bool Muted { get; set; }

    /// <summary>
    /// Um frame de microfone. O <see cref="VoiceEngine"/> sempre entrega 10ms
    /// (960 bytes, PCM 48kHz mono 16-bit); o que a implementacao faz com isso —
    /// mandar cru ou juntar e comprimir — e problema dela.
    /// </summary>
    void SendVoice(byte[] payload, int offset, int count);

    /// <summary>
    /// PCM 48kHz mono 16-bit recebido de alguem: (senderId, buffer, offset, tamanho).
    /// Dispara na thread de rede — nao toque na UI daqui.
    /// </summary>
    event Action<uint, byte[], int, int>? VoiceReceived;
}

/// <summary>
/// Canal separado para o som compartilhado. Voz e audio da tela nao podem cair no
/// mesmo jitter buffer: cada um tem volume, codec e continuidade proprios.
/// </summary>
public interface ISharedAudioTransport
{
    /// <summary>PCM 48kHz mono 16-bit produzido pela captura do sistema.</summary>
    void SendSharedAudio(byte[] payload, int offset, int count);

    /// <summary>PCM 48kHz mono 16-bit recebido de outro participante.</summary>
    event Action<uint, byte[], int, int>? SharedAudioReceived;
}
