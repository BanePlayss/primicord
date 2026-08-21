# PRIMICORD — ARQUITETURA ATUAL

Auditoria da Fase 0. Retrato do código como ele está em `57f5784`, sem
modificação nenhuma. Serve de linha de base para o [plano de migração](PLANO-DE-MIGRACAO.md).

> **ATUALIZAÇÃO 0.7.1 (21/08/2026):** o texto abaixo é a linha de base histórica,
> não o estado operacional da versão pública. Hoje salas, presença e chat usam
> réplicas SQLite descobertas pelo Tailscale; o Firestore ficou restrito a login,
> avatar e usuários do Primitivão. Voz (Opus 32 kbps), tela (JPEG delta) e áudio
> compartilhado (Opus 64 kbps) atravessam um relay WebSocket efêmero, sem gravação
> de mídia. A malha UDP permanece como compatibilidade. O shell público removeu
> campeonato, ranking e apostas e abre diretamente no canal geral.

---

## 1. Números

| | |
|---|---|
| Arquivos `.cs` | 31, todos na raiz — **zero pastas** |
| Linhas de C# | 9.027 |
| Namespaces | 1 (`Primicord`) |
| Interfaces declaradas | **0** |
| Projetos de teste | **0** |
| Commits | 13 |
| Alvo | `net10.0-windows10.0.19041.0`, WinForms, x64 self-contained |

### Distribuição do código

```
MainForm.cs        1518  ██████████████████  (16,8% de tudo)
RoomSession.cs      695  ████████
ScreenShare.cs      600  ███████
VoiceEngine.cs      426  █████
CinemaView.cs       386  ████
SettingsDialog.cs   377  ████
Cinema.cs           346  ████
DxgiCapture.cs      326  ███
PrimicordUi.cs      320  ███
Primitivao.cs       313  ███
CaptureTarget.cs    281  ███
Firestore.cs        259  ███
outros (19 arq.)   3180
```

---

## 2. Camadas reais (não as desejadas)

```
┌──────────────────────────────────────────────────────────────┐
│                         MainForm.cs                          │
│                         1518 linhas                          │
│                                                              │
│  É a UI, o Core, o orquestrador e a máquina de estados ao    │
│  mesmo tempo. Instancia e é dona de TUDO:                    │
│                                                              │
│   Firestore   RoomDirectory   Config     PrimitivaoUser      │
│   ChatService RoomSession     VoiceEngine ScreenSender       │
│   ScreenReceiver ClipRecorder MusicShare  CinemaSession      │
│   StageView   HotkeyBinding   ToastOverlay NotifyIcon        │
│                                                              │
│  Estado de navegação, lista de salas, membros, presença,     │
│  DMs abertas, quem está compartilhando, timers de poll.      │
└───────────────┬──────────────────────────────────────────────┘
                │ chamada direta de classe concreta, sempre
   ┌────────────┼────────────┬──────────────┬─────────────┐
   ▼            ▼            ▼              ▼             ▼
┌────────┐ ┌──────────┐ ┌──────────┐ ┌───────────┐ ┌──────────┐
│Firestore│ │RoomSession│ │VoiceEngine│ │ScreenSender│ │Cinema   │
│  REST   │ │ UDP mesh  │ │  NAudio   │ │ JPEG delta │ │Session  │
└────────┘ └──────────┘ └──────────┘ └───────────┘ └──────────┘
```

**Não existe camada Core.** Não existe fronteira entre UI e rede. A UI conhece
`RemotePeer.SenderId` (um hash FNV-1a de 32 bits do id do peer), monta pacote de
tela, liga evento de rede direto no controle WinForms. Trocar o transporte hoje
significa reescrever `MainForm`.

Os controles de UI (`PeerTile`, `RailItem`, `ChatView`, `StageView`,
`PrimicordUi`, `Glyphs`, `GlyphButton`) **estão bem separados** — são controles
burros que recebem propriedades. Essa parte da sua arquitetura alvo já existe.

---

## 3. Rede — protocolo UDP próprio

Está tudo em [`RoomSession.cs`](../RoomSession.cs). **Não é só voz** — este
socket carrega cinco coisas diferentes:

### Cabeçalho (9 bytes, little-endian)

```
 0        1                    5                    9
 ┌────────┬────────────────────┬────────────────────┬──────────
 │  tipo  │     senderId       │     sequência      │  payload
 │ 1 byte │      4 bytes       │      4 bytes       │
 └────────┴────────────────────┴────────────────────┴──────────
            FNV-1a do peerId
```

| Tipo | Nome | Payload | Quem consome |
|---|---|---|---|
| 0 | `Voice` | PCM cru, 960 B | `VoiceEngine` |
| 1 | `Punch` | vazio | furo de NAT / keepalive |
| 2 | `PunchAck` | vazio | furo de NAT |
| 3 | `Bye` | vazio | saída limpa |
| 4 | `Screen` | +10 B de sub-cabeçalho, fragmentado | `ScreenReceiver` |
| 5 | `Music` | PCM cru, 960 B | `VoiceEngine` (modo DJ) |
| 6 | `CinemaCtl` | JSON ≤1200 B | `CinemaSession` |
| 7 | `CinemaData` | pedaço de arquivo, índice na sequência | `CinemaSession` |

> **Isto é a peça central do custo de migração.** "Trocar por WebRTC" não é
> trocar a voz: é reimplantar tela fragmentada, áudio de sistema, controle de
> cinema e transferência de arquivo com NACK — quatro protocolos, não um.

### Travessia de NAT

`Stun.cs` faz Binding Request (RFC 5389) **pelo mesmo socket que leva a voz** —
o comentário no arquivo explica corretamente por que isso é obrigatório. O
endereço público + todos os IPv4 locais viram candidatos publicados no
Firestore; os dois lados furam a cada 250 ms até um pacote chegar de verdade,
e aí o endereço de origem vira `RemotePeer.Locked`.

**Limite documentado e real:** NAT simétrico dos dois lados = par não conecta.
Não há TURN. O app avisa na tela.

`RemotePeer.OnLan` detecta que o par fechou por endereço RFC 1918 e, se
*todos* os espectadores estão em LAN, o orçamento de tela sobe de 2 MB/s para
8 MB/s ([`ScreenShare.cs:66`](../ScreenShare.cs)).

---

## 4. Áudio — o achado mais importante da auditoria

[`VoiceEngine.cs`](../VoiceEngine.cs): PCM **48 kHz mono 16-bit sem compressão
nenhuma**, frames de 10 ms (960 bytes), jitter buffer de 50 ms por pessoa,
mixagem NAudio.

```
768 kbps por par, por direção
```

O comentário em [`RoomSession.cs:41`](../RoomSession.cs) afirma "com 6 pessoas
cada um sobe ~5×64 kbps (~320 kbps)". **Está errado por 12x.** O número real:

| Pessoas na sala | Subida por pessoa (só voz) |
|---|---|
| 2 | 0,8 Mbps |
| 4 | 2,4 Mbps |
| 6 | **4,0 Mbps** |
| 8 | 5,6 Mbps |

Somando o teto padrão de tela (`ScreenBudgetKb = 2000` → **16 Mbps**), uma sala
de 6 com alguém compartilhando pede ~20 Mbps de upload. Isso explica qualquer
relato de voz picotando quando entra gente ou quando alguém compartilha.

### Duas coisas que já estão pagas e não estão sendo usadas

1. **`SpeexDSPSharp 1.3.0` está no `.csproj` e tem ZERO referências no código**
   (`grep -r Speex *.cs` não retorna nada). Alguém já pretendeu cancelamento de
   eco / supressão de ruído e nunca ligou.
2. Não há **nenhuma** menção a Opus. O README declara "todo mundo de fone"
   porque não existe AEC — o SpeexDSP resolveria exatamente isso.

---

## 5. Tela

[`ScreenShare.cs`](../ScreenShare.cs) — codec próprio, e um bom:

- Captura por **DXGI Desktop Duplication** ([`DxgiCapture.cs`](../DxgiCapture.cs)),
  com queda para GDI quando falha. 4 ms/quadro.
- Grade de blocos de 128 px; só os blocos que mudaram viram JPEG e vão pela rede.
- 2 blocos por quadro reenviados em rodízio = conserto de perda sem keyframe.
- Escada adaptativa: primeiro cede qualidade (92→45), depois cede resolução
  (nativo→1280→1024→800).
- Sem retransmissão: quadro com pedaço perdido é descartado inteiro.

`ScreenReceiver` mantém um canvas persistente por remetente e aplica os blocos
por cima. Alvo pode ser monitor ou janela ([`CaptureTarget.cs`](../CaptureTarget.cs)).

**Não é H.264.** A justificativa no código (Media Foundation por COM ou FFmpeg
junto do exe) continua válida.

---

## 6. Firestore

Cliente REST próprio ([`Firestore.cs`](../Firestore.cs)) — não o SDK, porque
não existe SDK .NET desktop decente. **Sem listener em tempo real** (a Listen
API é gRPC), então tudo é polling.

### Coleções

```
primitivao/apostas          ← SÓ LEITURA. Fonte de verdade de nick/senha/saldo/tema
pc_rooms/{id}               ← sala de voz: name, createdBy, createdAt
pc_rooms/{id}/peers/{peer}  ← presença + candidatos: nick, pubIp, pubPort, locEps,
                              lastSeen, muted, sharing
pc_chat/geral/msgs/{id}     ← chat geral. id = "{timestamp:D13}-{rand}"
pc_dm/{a__b}/msgs/{id}      ← DMs
pc_presence/{nick}          ← online global: nick, lastSeen, room
```

### Custo de polling — dois laços independentes

**`MainForm.PollAsync`, a cada 2 s** ([`MainForm.cs:606`](../MainForm.cs)):
1. `pc_presence` (até 100 docs)
2. `pc_rooms`
3. `pc_rooms/{id}/peers` — **uma chamada por sala** (N+1)
4. `runQuery` das 60 mensagens do canal/DM aberto
5. heartbeat de presença a cada ~20 s

**`RoomSession.PresenceLoopAsync`, a cada 2 s**, quando em sala de voz:
6. escrita da própria presença
7. leitura de `pc_rooms/{id}/peers`

Com 5 salas existindo: **~10 requisições REST a cada 2 segundos**, ou ~18.000
leituras por hora de app aberto. Funciona hoje porque a liga é pequena. Não
escala para o modelo Servidor → Canais da arquitetura alvo, que multiplica
documentos.

---

## 7. Identidade e segurança

[`Primitivao.cs`](../Primitivao.cs): login é nick + SHA-256 da senha, conferido
**no cliente** contra o doc público `primitivao/apostas`. O hash fica em
`config.txt` para re-login silencioso.

**Não há Firebase Auth.** A consequência está honestamente documentada em
[`Chat.cs:32`](../Chat.cs):

> as DMs NÃO são privadas de verdade […] a rules não tem como saber quem está
> pedindo e a leitura precisa ficar pública.

O alcance real disso é maior do que o comentário admite. Sem identidade
verificada no servidor, as rules do `pc_rooms`/`pc_chat`/`pc_presence` precisam
aceitar escrita de qualquer um. Ou seja, hoje qualquer pessoa com a chave de
API (que está no fonte, e é pública por natureza) pode:

- ler todas as DMs de todo mundo;
- apagar qualquer sala ou qualquer presença;
- escrever mensagem se passando por outro nick;
- publicar candidatos de rede falsos e receber tráfego de voz.

Isso não é um bug de implementação — é o teto do modelo escolhido. **E é o
bloqueador direto da arquitetura alvo**: "Servidores com membros e permissões"
não existe sem autenticação de servidor.

⚠️ **Pendência operacional herdada:** o README diz que as rules do `pc_rooms`
nunca foram publicadas no Firebase Console. Sem isso o app não lista nem cria
sala. Confirmar antes de qualquer teste de campo.

---

## 8. Arquivos locais

```
%APPDATA%\Primicord\
├── config.txt          chave=valor em texto puro (14 chaves, Rooms.cs:98)
├── Clipes\             .avi gerados pelo AviWriter
├── Cinema\             filmes recebidos
└── vlc\                libVLC extraído do recurso embutido na 1ª sessão cinema

%TEMP%\Primicord\primicord.log   truncado em 2 MB
```

`PRIMICORD_DATA_DIR` redireciona tudo — existe porque um harness de teste
apagava o `config.txt` real.

---

## 9. Build e distribuição

`build.ps1` → `dotnet publish` self-contained, single-file, comprimido →
`Primicord.exe` (~56 MB). O libVLC (~100 MB) é empacotado enxuto num
`vlc-runtime.zip` embutido como recurso e se extrai sozinho na primeira sessão
cinema ([`VlcRuntime.cs`](../VlcRuntime.cs)).

Não é assinado — SmartScreen avisa na primeira execução. Não há instalador.

### Dependências

| Pacote | Uso |
|---|---|
| `NAudio 2.3.0` | captura/reprodução/mixagem |
| `Vortice.Direct3D11` + `Vortice.DXGI 3.8.3` | captura pela GPU |
| `LibVLCSharp.WinForms` + `VideoLAN.LibVLC.Windows` | sessão cinema |
| `SpeexDSPSharp 1.3.0` | **nenhum — dependência morta** |

---

## 10. Funcionalidades além do README

O README está desatualizado (lista Fase 2 e 3 como pendentes). Já existe e funciona:

- Tela com codec delta + captura GPU + captura de janela
- Clipes com buffer rolante e hotkey global (`RegisterHotKey`, não hook de teclado
  — decisão certa, explicada em [`Hotkeys.cs`](../Hotkeys.cs))
- Modo DJ (áudio do sistema via process loopback, excluindo o próprio processo)
- Sessão cinema (envio do arquivo + NACK + reprodução sincronizada)
- Chat geral + DMs + presença global
- Shell no formato Discord, login do Primitivão, tema vindo do site, bandeja

---

## 11. Dívidas e riscos catalogados

Ordenados por impacto na migração.

### 🔴 Bloqueadores da arquitetura alvo

1. **`MainForm` é o Core.** 1.518 linhas fazendo UI + orquestração + estado.
   Qualquer troca de transporte passa por reescrever este arquivo.
2. **Sem autenticação de servidor.** Impede permissões, DMs privadas e o modelo
   Servidor→Canal. (§7)
3. **O socket UDP carrega 5 protocolos.** Migrar para WebRTC não é migrar a voz. (§3)
4. **Zero testes.** O código tem afordâncias de teste (`AddPeerDirect`,
   `StartNetworkOnlyAsync`, `PRIMICORD_DATA_DIR`) que referenciam um harness que
   **não está no repositório**. Refatorar 9.000 linhas às cegas.

### 🟠 Correções de alto retorno e baixo custo

5. **Voz sem compressão** — 768 kbps/par. Opus a 32 kbps é uma redução de 24x. (§4)
6. **`SpeexDSPSharp` pago e não usado** — AEC e supressão de ruído estão a um
   `using` de distância. Acabaria com a regra "todos de fone".
7. **Comentário de banda errado por 12x** em `RoomSession.cs:41`. Está guiando
   decisão de design com número falso.

### 🟡 Corrosão que a refatoração vai expor

8. **`RemotePeer` é mutado sem sincronização.** `Peers` devolve cópia da lista
   sob lock, mas `Locked`, `Nick`, `Candidates` e `Muted` são escritos na thread
   de rede e lidos na thread de UI. Funciona por sorte de layout de memória.
9. **`RefreshCandidates` limpa `Candidates` durante o poll** enquanto
   `PunchTick` itera sobre a mesma lista em outra thread.
10. **`StartAsync` e `StartNetworkOnlyAsync` duplicam** a montagem do socket
    inteira (buffers, bind, STUN, thread de rx).
11. **Polling N+1 no Firestore** (§6).
12. **`Config` é um parser chave=valor manual** com 14 casos em `switch`.
    Cada preferência nova é editar 3 lugares.
13. **README desatualizado** — declara pendente o que já está pronto (§10).

### ✅ O que está bom e não deve ser tocado

- Separação dos controles de UI (`PeerTile`, `RailItem`, `ChatView`, `Pv`).
- O codec de tela por blocos delta — é engenharia de verdade, com adaptação
  em dois eixos.
- A camada de captura (`DxgiCapture` com queda para GDI).
- As decisões documentadas com o *porquê* (STUN no mesmo socket, `RegisterHotKey`
  em vez de hook, mandar o arquivo em vez da tela no cinema, `Primitivao`
  somente-leitura). A densidade de comentário explicativo neste código está
  acima da média — preservar isso na refatoração.
