# PRIMICORD

O Discord dos primitivos — app nativo de Windows pra sala de voz em grupo,
compartilhar tela e tirar clipe dos últimos segundos.

Irmão do [CherrySpy](https://github.com/BanePlayss/duovoz) (que é 1-pra-1 e só na
LAN). O Primicord é feito pra **N pessoas pela internet**.

## Como funciona

- **Voz**: malha UDP ponto-a-ponto — cada um manda direto pra cada outro. Não tem
  servidor no meio, ninguém paga hospedagem e nenhum áudio passa por terceiros.
- **Encontro**: o Firestore do projeto `primitivao` (coleção `pc_rooms`) guarda só
  a lista de salas, quem está em cada uma e o IP:porta público de cada um.
- **NAT**: cada cliente descobre seu endereço público por STUN e os dois lados furam
  o NAT mandando pacotes ao mesmo tempo (hole punching).
- **Áudio**: 48kHz mono, frames de 10ms, jitter buffer por pessoa, mixados com
  NAudio. Na malha vai PCM cru (798 kbps por par); com `webrtc=1` vai Opus por
  RTP/SRTP (49 kbps por par, e criptografado).

### Limite conhecido

Se os **dois** lados estiverem atrás de NAT simétrico (comum em 4G e alguns
provedores), o furo não acontece e o par não conecta — só um relay (TURN)
resolveria. O app avisa na tela em vez de ficar mudo sem explicação.

### Eco

Tem cancelamento de eco (Speex AEC + supressão de ruído), ligado por padrão.
Medido ~16 dB de atenuação num caminho sintético — dá pra usar caixa de som sem
devolver a voz dos outros pra sala.

**Fone ainda é melhor**, por um motivo que ajuste nenhum resolve: o cancelador só
remove o que o *Primicord* tocou. Som de jogo, Spotify ou qualquer outra coisa do
sistema que vaze no microfone continua vazando, porque não temos esse sinal como
referência. É a mesma limitação de qualquer AEC de aplicativo.

Se atrapalhar em algum microfone: `aec=0` no `config.txt`. E `agc=1` liga o
nivelamento automático de volume — desligado por padrão porque derruba o
cancelamento de eco de 16 dB pra 2,5 dB; só vale pra quem usa fone.

## Rodar

Só abrir o `Primicord.exe` — é **um arquivo só** e não precisa de .NET instalado
na máquina (self-contained, ~100MB, dos quais 38MB são o motor de vídeo do
cinema). Na primeira vez o Windows mostra o aviso do SmartScreen ("Mais
informações" → "Executar assim mesmo"), porque o exe não é assinado.

Pra gerar o exe de novo depois de mexer no código:

```powershell
./build.ps1
```

Ou rodar direto do código:

```
dotnet run --project Primicord.csproj
```

## Testar

```
dotnet test tests/Primicord.Tests
```

Sobe a malha UDP em loopback e duas conexões WebRTC no mesmo processo — dá pra
verificar o caminho de voz inteiro sem precisar de dois PCs. Não toca no
Firestore nem na internet.

## Atualizar

O app se atualiza sozinho: **Configurações → ATUALIZAR → PROCURAR ATUALIZAÇÃO**.
Ele consulta as releases do GitHub, baixa a versão nova, se troca no lugar e
reabre. O primeiro clique só procura; o segundo é que baixa — pra ninguém levar
100MB de surpresa.

Pra publicar uma versão nova:

1. Sobe o `<Version>` no `Primicord.csproj` (é ele que o app compara com a tag).
2. `./build.ps1`
3. `gh release create v0.2.0 Primicord.exe --title "0.2.0" --notes "o que mudou"`

O anexo **precisa** se chamar `Primicord.exe`. Se junto vier um
`Primicord.exe.sha256`, o app confere o hash antes de trocar.

Onde ele procura sai de `updaterepo=dono/repo` no `config.txt`. O repositório
tem que ser **público** — senão o download exigiria token, e token embutido em
binário que se distribui por aí não é opção.

## Estado

- [x] Salas de voz em grupo pela internet
- [x] Compartilhar tela (captura por GPU, codec de blocos delta)
- [x] Clipes (buffer rolante + hotkey global)
- [x] Chat, DMs, modo DJ, sessão cinema
- [x] Câmera na sala (JPEG nativo da webcam, sem re-encodar)
- [x] Voz por WebRTC com Opus (`webrtc=1`)
- [x] Cancelamento de eco
- [x] Atualização pelo próprio app

Ver [docs/PLANO-DE-MIGRACAO.md](docs/PLANO-DE-MIGRACAO.md) pro que vem depois.

**Passo manual pendente:** publicar as rules do `pc_rooms` no Firebase Console,
cobrindo `peers/**` e — se for usar `webrtc=1` — também `signal/**`. Sem isso o
app não lista nem cria sala.
