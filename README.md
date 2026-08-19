# PRIMICORD

O Discord dos primitivos — app nativo de Windows pra sala de voz em grupo,
compartilhar tela e tirar clipe dos últimos segundos.

## 0.6.10 — servidor configurado automaticamente

- Ao marcar este PC como host, o Primicord detecta o IP conectado do Tailscale.
- O endereço completo `http://100.x.y.z:8765` aparece pronto para compartilhar.
- Endereços digitados sem protocolo ou porta são normalizados ao salvar.

## 0.6.9 — rede privada e mini servidor

- O instalador do Primicord instala o Tailscale quando ele ainda não existe.
- Atualizações diferenciais também oferecem o onboarding dentro do app.
- Um mini servidor local com SQLite assume salas, presença, chat e sinalização.
- O PC servidor inicia o coordenador junto com o login do Windows.
- O servidor só aceita loopback e endereços da tailnet (`100.64.0.0/10` e IPv6 do Tailscale).
- A malha publica primeiro o endereço Tailscale; STUN continua como plano B.
- Firestore permanece como compatibilidade temporária e como leitura dos dados do site.
- Nenhuma auth key do Tailscale é embutida no executável.

## 0.6.8 — login resiliente e economia de cota

- Identifica corretamente `429 / Quota exceeded`, sem chamar de falta de internet.
- Abre com a identidade salva quando o servidor está temporariamente indisponível.
- Aplica recuo progressivo e para de repetir requisições durante o bloqueio.
- Reduz a frequência de salas, presença, campeonato e heartbeat.
- O chat mantém um cursor e busca somente mensagens novas depois da primeira carga.
- Não consulta mais uma DM inexistente enquanto a tela do Primitivão está aberta.

## 0.6.7 — sala social redesenhada

- Arena central maior, sem o antigo “cartão dentro do Discord”.
- Lista de salas e ranking compacto na coluna esquerda.
- Chat da sala e atividade real dos participantes na coluna direita.
- Ferramentas de câmera, tela, buffer, clipe, DJ e cinema no cabeçalho.
- Barra inferior dedicada a microfone, áudio, convite e saída.
- Avatares com halo de voz mais forte e limite para não invadir as instruções.

Irmão do [CherrySpy](https://github.com/BanePlayss/duovoz) (que é 1-pra-1 e só na
LAN). O Primicord é feito pra **N pessoas pela internet**.

## Como funciona

- **Voz**: malha UDP ponto-a-ponto — cada um manda direto pra cada outro. Não tem
  servidor no meio, ninguém paga hospedagem e nenhum áudio passa por terceiros.
- **Encontro**: o mini servidor do Primicord guarda salas, presença, posições,
  chat e sinalização em SQLite. Durante a migração, o Firestore ainda entra como
  compatibilidade quando o servidor configurado não responde.
- **NAT**: o endereço da tailnet é o primeiro candidato. O Tailscale escolhe a rota
  direta ou relay; STUN e hole punching continuam como plano B.
- **Áudio**: 48kHz mono, frames de 10ms, jitter buffer por pessoa, mixados com
  NAudio. Na malha vai PCM cru (798 kbps por par); com `webrtc=1` vai Opus por
  RTP/SRTP (49 kbps por par, e criptografado).

### Configurar o mini servidor

1. Todos instalam o Tailscale e entram na mesma tailnet.
2. No PC que ficará ligado: **Configurações → Rede privada → Este PC hospeda**.
3. Nos demais PCs, informe `http://100.x.y.z:8765` ou o nome MagicDNS do servidor.
4. Reabra o Primicord depois de trocar o endereço do mini servidor.

O banco fica em `%LOCALAPPDATA%\PrimicordServer\primicord.db`. Áudio, câmera e
tela não passam pelo servidor: continuam ponto a ponto.

### Limite conhecido do modo de compatibilidade

Sem Tailscale, se os **dois** lados estiverem atrás de NAT simétrico (comum em
4G e alguns provedores), o furo pode não acontecer. Com Tailscale ele usa a rota
privada e o relay da própria tailnet quando a conexão direta não fecha.

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

Sobe a malha UDP em loopback, duas conexões WebRTC e testa o banco SQLite do mini
servidor — dá pra verificar o caminho principal sem dois PCs. Não toca no
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
- [x] Espaço social por sala (avatares livres, tamanho, proximidade e chat próprio)

Ver [docs/PLANO-DE-MIGRACAO.md](docs/PLANO-DE-MIGRACAO.md) pro que vem depois.

**Passo manual pendente:** publicar as rules do `pc_rooms` no Firebase Console,
cobrindo `peers/**` e — se for usar `webrtc=1` — também `signal/**`. Sem isso o
app não lista nem cria sala. A posição social reutiliza `peers/**`, então não
exige regra adicional na série 0.6.x.
