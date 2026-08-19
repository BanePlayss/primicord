# PRIMICORD — PLANO DE MIGRAÇÃO

Entrega da Fase 0, par da [arquitetura atual](ARQUITETURA-ATUAL.md).

Este plano parte da sua proposta de 10 fases e **muda quatro coisas**. As
mudanças estão justificadas na §1; se você discordar de alguma, a ordem original
continua executável — só fica mais cara nos pontos indicados.

> **DECISÃO ATUALIZADA (19/08/2026):** o dono do projeto decidiu adotar Tailscale
> e reduzir o uso do Firestore. A 0.6.9 entrega o instalador integrado, candidatos
> `100.64/10`, mini servidor local com SQLite e Firestore como compatibilidade.
> A voz por WebRTC já estava implementada e foi preservada.

---

## 1. O que eu mudaria na sua proposta, e por quê

### 1.1 Tailscale e WebRTC resolvem o mesmo problema — e você escolheu os dois

A sua ordem é: Arquitetura → Tailscale → WebRTC → Files → UI.

O WebRTC existe para resolver três coisas: **travessia de NAT** (ICE/STUN/TURN),
**codecs** (Opus, VP8/H.264) e **controle de congestionamento**.

Se o Tailscale entrar antes, a travessia de NAT deixa de ser problema: todo
mundo ganha um `100.x.x.x` estável, o WireGuard já fura o NAT, e quando não
consegue cai sozinho no relay DERP. O limite conhecido do Primicord — NAT
simétrico dos dois lados — **morre na Fase Tailscale**, não na Fase WebRTC.

O que sobraria do WebRTC depois disso: os codecs e o congestion control. E o
codec você consegue muito mais barato:

| | Hoje | Trocando só o codec | Migrando pra WebRTC |
|---|---|---|---|
| Voz, 6 pessoas | 4,0 Mbps | **0,2 Mbps** (Opus 32k) | 0,2 Mbps |
| Eco | sem AEC | **SpeexDSP AEC** | AEC embutido |
| Custo | — | ~2 dias, isolado em `VoiceEngine` | reescrever 5 protocolos |

**Recomendação:** parar de tratar "WebRTC" como uma fase e tratá-lo como uma
*decisão* tomada depois do Tailscale. Quebrar em duas entregas independentes:

- **Opus + AEC** sobre a malha UDP que já existe → ganho de 24x na banda e fim
  da regra "todo mundo de fone", sem tocar em arquitetura.
- **WebRTC completo** → só se, depois de Tailscale + Opus, ainda faltar algo.

Não estou dizendo que WebRTC é errado. Estou dizendo que, na sua ordem, ele
custa a reescrita de tela + música + cinema + arquivos (§3 da auditoria) para
entregar o que Tailscale e Opus já entregaram duas fases antes.

### 1.2 "One-Click Tailscale" tem um problema que o plano não vê

O plano diz: instalador → Private Network Manager → Tailscale → pronto.

Na prática o cliente Windows do Tailscale é um MSI que instala um **serviço de
sistema** (pede admin) e depois exige `tailscale up`, que abre o navegador para
o login. Para virar um clique de verdade só existem dois caminhos:

1. **Auth key pré-autorizada embutida no exe.** Funciona — e significa que
   qualquer pessoa que receber o `Primicord.exe` entra na sua tailnet. É uma
   chave de rede privada dentro de um binário não assinado que vocês mandam por
   Discord. Não faça.
2. **Uma chave efêmera por pessoa**, gerada por vocês e colada uma vez no
   primeiro boot. Deixa de ser um clique, vira um clique + um copiar/colar.
   É o caminho honesto.

Somar a isso: dependência de conta de terceiro por pessoa e limites de plano
gratuito (**confirmar os limites atuais antes de assumir** — o plano pessoal
historicamente cobre poucos usuários, e vocês são exatamente esse tamanho).
Alternativa sem conta: **Headscale** (servidor de coordenação self-hosted), que
custa uma VPS — contradizendo o "ninguém paga hospedagem" do README.

**Recomendação:** o Tailscale entra como caminho *preferencial*, **mantendo a
malha STUN atual como plano B**. Custo de manter: zero, o código já existe e
já funciona. Ganho: o app nunca fica inutilizável porque alguém não instalou a
VPN, e — o mais importante — a sua interface `IPrivateNetwork` passa a ter
**duas implementações reais desde o primeiro dia**, em vez de ser uma abstração
sobre um único caso.

### 1.3 A Fase 1 deveria extrair o Core, não escrever interfaces

Sua Fase 1 cria sete interfaces (`IPrivateNetwork`, `ISignaling`,
`IPeerConnection`, `IAudioTransport`, `IVideoTransport`, `IFileTransfer`,
`IRoomService`) e não implementa nada ainda.

O problema real não é falta de interface — é que **`MainForm.cs` tem 1.518
linhas e É o Core**. Interface com uma implementação e um consumidor não desacopla
nada: só adiciona um arquivo entre duas coisas que continuam casadas. E
interfaces desenhadas *antes* de existir a segunda implementação saem no
formato da primeira — no seu caso, no formato de um WebRTC que talvez não venha.

**Recomendação:** extrair estado e orquestração de `MainForm` para classes de
Core concretas primeiro. Criar interface só nas duas costuras onde a segunda
implementação é certa:

- `IPrivateNetwork` → Tailscale **e** STUN direto (as duas existem, §1.2)
- `IDirectory` / `ISignaling` → Firestore hoje, e é o que você vai querer trocar
  quando o polling doer

As outras cinco: escrever quando a segunda implementação estiver a uma sprint
de distância, não antes.

### 1.4 Autenticação é P0, não polimento

A arquitetura alvo tem Servidores com membros. O plano nunca menciona
autenticação.

Hoje não existe Firebase Auth: o login é conferido no cliente, então as rules
não sabem quem está pedindo e precisam liberar leitura e escrita para todo
mundo. Consequência atual, já em produção: **qualquer um lê todas as DMs, apaga
qualquer sala e escreve se passando por qualquer nick** (§7 da auditoria).

"Servidor com membros e permissões" não é implementável em cima disso. Não é
uma questão de caprichar depois — é o alicerce do modelo de dados inteiro.

**Recomendação:** Firebase Auth (custom token assinado, ou anônimo + custom
claims) entra antes de Servidores/Canais e antes de Chat completo.

---

## 2. Ordem revisada

```
              ┌──────────────────────────────────────┐
   PRONTO ──▶ │ F0  Auditoria                        │  ← este documento
              └───────────────┬──────────────────────┘
                              ▼
              ┌──────────────────────────────────────┐
        🔴 P0 │ F1  Harness de protocolo             │  rede de segurança
              └───────────────┬──────────────────────┘
                              ▼
              ┌──────────────────────────────────────┐
        🔴 P0 │ F2  Opus + AEC                       │  24x de banda, fim do eco
              └───────────────┬──────────────────────┘
                              ▼
              ┌──────────────────────────────────────┐
        🔴 P0 │ F3  Tailscale (com STUN de plano B)  │  mata o NAT simétrico
              └───────────────┬──────────────────────┘
                              ▼
              ┌──────────────────────────────────────┐
        🔴 P0 │ F4  Extrair o Core do MainForm       │  ← "Arquitetura" de verdade
              └───────────────┬──────────────────────┘
                              ▼
              ┌──────────────────────────────────────┐
        🟠 P1 │ F5  Autenticação                     │  destrava tudo abaixo
              └───────────────┬──────────────────────┘
                              ▼
              ┌──────────────────────────────────────┐
        🟠 P1 │ F6  Servidores e Canais              │  modelo + UI
              └───────────────┬──────────────────────┘
                              ▼
              ┌──────────────────────────────────────┐
        🟠 P1 │ F7  Arquivos P2P                     │  generaliza o Cinema
              └───────────────┬──────────────────────┘
                              ▼
              ┌──────────────────────────────────────┐
        🟠 P1 │ F8  Chat completo                    │
              └───────────────┬──────────────────────┘
                              ▼
              ┌──────────────────────────────────────┐
        🟡 P2 │ ◆ PORTÃO: WebRTC ainda é necessário? │
              └───────────────┬──────────────────────┘
                              ▼
              ┌──────────────────────────────────────┐
        🟡 P2 │ F9  Tela: rever codec                │
              └───────────────┬──────────────────────┘
                              ▼
              ┌──────────────────────────────────────┐
        🟢 P3 │ F10 Clipes, polimento, instalador    │
              └──────────────────────────────────────┘
```

Comparando com a sua ordem: **Tela e Clipes caem para o fim porque já
funcionam** (§10 da auditoria — o README está desatualizado). Nova UI deixa de
ser fase própria e vira parte da F6, porque a UI só muda de verdade quando o
modelo Servidor→Canal existe.

---

## 3. As fases

Cada uma tem critério de saída testável. Nenhuma fase começa antes da anterior
passar no seu critério.

---

### F1 — Harness de protocolo 🔴 ✅ FEITO

**Entregue:** `tests/Primicord.Tests` — 21 testes, ~6 s, sem rede externa e sem
Firestore. Rode com:

```bash
dotnet test tests/Primicord.Tests
```

O que cobre:

| Arquivo | O quê |
|---|---|
| `ProtocolTests.cs` | Malha UDP em loopback: punch/ack, voz íntegra, mudo, quadro de tela fragmentado remontando byte a byte, `Bye`, `HashId` |
| `StunTests.cs` | `ParseResponse` aceitando XOR/legado e **recusando** transação errada, cookie errado, pacote de voz e pacote curto |
| `WebRtcTests.cs` | Duas `WebRtcVoiceMesh` reais negociando e passando áudio Opus, mudo, e o par sumindo da presença |

Duas coisas que apareceram no caminho:

- **O `.csproj` do app engolia os testes.** O glob padrão do SDK é `**/*.cs` a
  partir da pasta do projeto, e `tests/` mora lá dentro — o app tentava compilar
  `Fact`/`Theory` sem referência ao xunit. Resolvido com `<Compile Remove="tests/**" />`.
  É provavelmente parente do problema que derrubou o harness anterior.
- **`WebRtcSignaling` virou `IWebRtcSignaling`.** Para os testes subirem duas
  malhas sem tocar no Firestore de produção. É uma das duas costuras que a §1.3
  já marcava como justificadas.

**Verificado que a suíte falha:** mutei `Stun.ParseResponse` (removendo a
checagem de transação) e `IsOfferer` (fazendo os dois lados ofertarem, o glare
clássico). Os dois testes certos falharam e os outros 9 seguiram verdes — a
suíte detecta com precisão, não por acaso. Mutações revertidas.

> **Isto também é a resposta pra "como eu testo sem 2 PCs".** O caminho completo
> de voz WebRTC — oferta, resposta, candidatos, DTLS, Opus ida e volta — roda numa
> máquina só, em 6 segundos.

<details>
<summary>Motivação original</summary>

**Por quê:** 9.000 linhas, zero testes, e o código já tem afordâncias de teste
(`RoomSession.AddPeerDirect`, `StartNetworkOnlyAsync`, `PRIMICORD_DATA_DIR`)
apontando para um harness que não está no repositório. Tudo depois disto é
refatoração de rede. Refatorar rede sem teste é apostar.

**Escopo (pequeno de propósito):**
- Projeto `Primicord.Tests` fora do `.csproj` do app (cuidado com o
  `NETSDK1151` que já mordeu antes — ver comentário no `.csproj`).
- Duas `RoomSession` em loopback via `StartNetworkOnlyAsync(useStun: false)`.
- Casos: punch/ack fecha; voz chega íntegra; quadro de tela fragmentado remonta
  byte a byte; `Bye` remove o peer; chunk duplicado é ignorado; chunk atrasado
  de quadro velho não corrompe o novo.
- `Stun.ParseResponse` com vetores fixos (é `public static`, testa direto).

**Pronto quando:** `dotnet test` roda verde em máquina limpa e um `git revert`
proposital de qualquer commit de rede faz pelo menos um teste falhar.

</details>

---

### F2 — ~~Opus + AEC~~ 🔴 ✅ FEITO

**Opus: feito**, mas por outro caminho — veio junto do WebRTC (§6) em vez de
entrar na malha antiga. Medido: 798 → 49,2 kbps por par. O comentário errado de
banda em `RoomSession.cs:41` foi corrigido.

**AEC: feito.** [MicPreprocessor.cs](../MicPreprocessor.cs), ligado por padrão
(`aec=0` desliga). O `SpeexDSPSharp` estava no `.csproj` desde o primeiro commit
com zero referências; agora está em uso. Roda em `VoiceEngine`, **antes** do
transporte, então vale igual para a malha UDP e para o WebRTC.

Vale registrar, porque é fácil supor o contrário: **o WebRTC não trouxe AEC.** O
WebRTC-o-padrão tem cancelamento de eco porque quem faz isso é o *navegador*. O
SIPSorcery é só transporte.

**Medido** (caminho de eco sintético: 50 ms de atraso, ganho 0,5):

| Configuração | Cancelamento |
|---|---|
| AEC puro | 16,0 dB |
| AEC + denoise | 16,1 dB |
| AEC + denoise + **AGC** | **2,5 dB** |

**Dois achados que mudaram o desenho:**

1. **`SPEEX_PREPROCESS_SET_AGC_LEVEL` é o único desses controles que o Speex lê
   como `float`.** Passar um `int` não dá erro nenhum — os bytes são
   reinterpretados, o alvo vira absurdo e o AGC **zera o microfone**. Voz muda
   para todo mundo, sem uma linha de log, com o AEC ligado por padrão. O teste
   pegou antes de sair.

2. **O AGC come 13,5 dB do cancelamento.** O `SpeexDSPSharp` não expõe o handle
   nativo do cancelador, então não dá para ligar o preprocessador nele
   (`SPEEX_PREPROCESS_SET_ECHO_STATE`). Sem esse elo o preprocessador não sabe o
   que é resíduo de eco e o AGC o trata como voz baixinha — amplificando de volta
   o que o AEC acabou de tirar. Por isso **o AGC vai desligado** (`agc=1` liga,
   e só faz sentido para quem usa fone). Se um dia o nivelamento com eco virar
   necessidade, o caminho é `NativeSpeexDSP` direto e fazer o elo na mão.

O teste do eco roda na configuração **padrão**, de propósito: se alguém ligar o
AGC por padrão, o cancelamento desaba para 2,5 dB e o teste quebra na hora.

**Limitação que fica:** só cancela o que o Primicord tocou. Som de jogo ou
Spotify vazando no microfone continua vazando — não temos esse sinal como
referência. É a mesma limitação de qualquer AEC de aplicativo.

**Falta:** validar em campo, com alguém realmente em caixa de som.

---

### F3 — Tailscale, com a malha atual como plano B 🔴 ✅ 0.6.9

**Por quê:** mata o limite de NAT simétrico, dá IP estável (que torna TCP
trivial na F7) e é a primeira peça da arquitetura alvo que muda o que o app
consegue fazer.

**Entregue na 0.6.9:**

- Setup integrado baixa e instala o MSI oficial quando necessário; o atualizador
  diferencial também tem onboarding. Não existe auth key embutida.
- Endereços Tailscale são publicados primeiro; LAN/STUN continuam candidatos.
- Mini servidor ASP.NET Core + SQLite para `pc_rooms`, `pc_presence`, `pc_chat`,
  `pc_dm` e sinalização. Só aceita loopback e tailnet.
- Configuração de host/endpoint na UI e inicialização automática no PC servidor.
- Firestore continua como fallback temporário durante a adoção pelos participantes.

**Onboarding entregue na 0.6.13:**

- O app baixa o Setup público e gera localmente uma cópia privada para o grupo.
- Uma senha compartilhada cifra a auth key reutilizável e o endpoint do servidor.
- O Setup privado instala o Tailscale, autentica o dispositivo e grava o endpoint.
- O segredo não entra no repositório, release, `config.txt` ou linha de comando.

**Failover entregue na 0.6.14:**

- Todo cliente inicia uma réplica SQLite e descobre as demais pelo status local
  do Tailscale, sem precisar de API administrativa ou servidor público.
- Escritas são espelhadas; leituras migram imediatamente para a próxima réplica.
- Anti-entropia por snapshot atualiza PCs que voltaram depois de ficar offline.
- Exclusões persistem como tombstones e não reaparecem por causa de banco antigo.
- A mídia continua na malha ponto a ponto, portanto a troca de coordenador não
  reinicia o áudio entre os participantes restantes.

**Escopo original preservado como referência:**
- `IPrivateNetwork` com **duas** implementações: `TailscaleNetwork` e
  `DirectStunNetwork` (o código atual, extraído). Contrato mínimo:
  `Status`, `LocalAddress`, `EnsureUpAsync()`, `CandidatesFor(peer)`.
- Detecção: `tailscaled` rodando? interface `100.64.0.0/10` presente?
  Se sim, publica o IP da tailnet como candidato **primeiro** na lista.
  `RemotePeer.Locked` já adota o primeiro endereço que responder — a lógica de
  seleção existente serve sem mudança.
- Onboarding: instalação silenciosa do MSI + chave efêmera por pessoa (§1.2).
  **Não embutir auth key no exe.**
- `RemotePeer.OnLan` precisa aprender que `100.64/10` também é "rede privada",
  senão a sessão via tailnet não ganha o orçamento alto de tela
  (`AppEnv.IsPrivateAddress`, `ScreenShare.cs:249`).

**Pronto quando:** dois PCs em provedores diferentes, pelo menos um deles atrás
de NAT simétrico (4G serve), conectam voz e tela. E: com o Tailscale desligado
nos dois, a sessão continua funcionando pelo caminho antigo.

---

### F4 — Extrair o Core 🔴

**Por quê:** é a "Arquitetura" da sua Fase 1, feita na ordem que funciona —
depois que existem duas implementações reais de rede para justificar a costura,
e com testes para segurar a queda.

**Escopo:**

```
Primicord/
├── Core/
│   ├── AppState.cs        estado que hoje são 20 campos de MainForm
│   ├── Identity.cs        PrimitivaoUser + sessão
│   ├── RoomModel.cs       sala, membro, presença
│   ├── VoiceController.cs entrar/sair/mutar/compartilhar
│   └── Events.cs          eventos que a UI assina
├── Net/
│   ├── IPrivateNetwork.cs + Tailscale/ + DirectStun/
│   ├── RoomSession.cs
│   └── Signaling/         IDirectory + FirestoreDirectory
├── Media/                 VoiceEngine, ScreenSender/Receiver, Clip, Music, Cinema
└── Ui/                    o que já está separado, movido pra cá
```

Regra que fecha a fase: **`MainForm` não pode mais referenciar `Firestore`,
`RoomSession` nem `Socket` diretamente.** Só `Core`.

**Pronto quando:** `MainForm.cs` abaixo de 600 linhas, `grep -c "Firestore\|RoomSession"
MainForm.cs` = 0, e a suíte da F1 passando sem alteração nos testes.

**Aproveitar a passagem:** consertar as corridas de thread em `RemotePeer`
(itens 8 e 9 da auditoria) — a extração toca exatamente esse código.

---

### F5 — Autenticação 🟠

**Por quê:** §1.4. Bloqueia F6 e F8.

**Escopo:**
- Firebase Auth. Sem backend próprio, o caminho é anônimo + vínculo com o nick
  do Primitivão, ou custom token — o que exige alguém assinando, ou seja, uma
  Cloud Function. **Decidir isto explicitamente**: é o único ponto do projeto
  que pode exigir hospedagem.
- Rules reescritas: leitura de DM só pelos dois participantes; escrita de
  mensagem só com `request.auth.uid` batendo com o autor; presença só a própria;
  sala só quem criou apaga.
- Migração dos dados existentes de `pc_chat` / `pc_dm` / `pc_presence`.

**Pronto quando:** um cliente com a API key mas sem login autenticado recebe
`PERMISSION_DENIED` ao tentar ler uma DM alheia — testado de verdade, com curl.

**Não fazer:** continuar escrevendo em `primitivao/apostas`. Aquele doc é
somente-leitura para este app e essa decisão está certa.

---

### F6 — Servidores e Canais 🟠

**Por quê:** é aqui que o Primicord vira o que a arquitetura alvo descreve. A
"Nova UI" da sua Fase 4 é consequência disto, não uma fase separada — o rail já
existe (`RailItem.cs` já tem `Kind.TextChannel` e `Kind.Voice`).

**Escopo:**
- Modelo no Core: `Server → { TextChannels, VoiceChannels, Members }`.
- Firestore: `servers/{id}/channels/{id}` + `members`. **Consolidar presença num
  documento agregado por servidor** em vez de um doc por peer — resolve o N+1
  descrito em §6 da auditoria antes que ele piore.
- Migrar `pc_rooms` para canais de voz de um servidor padrão.
- UI: rail com servidores, canais de texto e de voz.

**Pronto quando:** dois servidores coexistem, com canais próprios, e o poll do
Firestore cabe em **duas** requisições por ciclo, não em 3+N.

---

### F7 — Arquivos P2P 🟠

**Por quê:** e a boa notícia: **metade já existe**. `CinemaSession` já faz
chunking, NACK, remontagem e progresso sobre o socket furado. Isto é
generalização, não implementação do zero.

**Escopo:**
- Extrair de `Cinema.cs` um `FileTransferSession` genérico (oferta → aceite →
  chunks → NACK → checksum → resume). O cinema passa a ser um consumidor dele.
- Retomada: hoje um envio interrompido recomeça. Persistir o bitmap de chunks
  recebidos no disco.
- Checksum SHA-256 no fim.
- **Sobre TCP/QUIC:** com a F3 pronta, cada peer tem IP estável na tailnet e um
  `TcpListener` funciona sem furo de NAT nenhum. O comentário em `Cinema.cs:21`
  ("não usamos TCP porque exigiria furar o NAT de novo") deixa de valer dentro
  da tailnet. Fora dela, o caminho UDP+NACK continua sendo o certo.

**Pronto quando:** 10 GB transferidos com o cabo de rede arrancado no meio e
religado, terminando com checksum correto.

---

### F8 — Chat completo 🟠

Histórico paginado, reply, edição, exclusão, timestamps agrupados, notificações.
Barato depois de F5 e F6. Sem exagerar: sem emoji custom, sem threads, sem
reações — isso é F10 se alguém sentir falta.

**Pronto quando:** rolar para trás carrega páginas antigas sem baixar a coleção
inteira, e uma mensagem editada aparece editada nos outros clientes.

---

### ◆ PORTÃO — WebRTC ainda é necessário? 🟡

Parar e medir antes de gastar a fase mais cara do projeto. Perguntas que só
podem ser respondidas aqui, com o app rodando:

1. Depois de Tailscale + Opus, alguém ainda tem problema de conexão? *(se não,
   ICE não serve pra nada)*
2. A voz ainda pica sob perda de pacote? *(se sim, o que falta é FEC/PLC —
   Opus já tem os dois, é ligar)*
3. A tela ainda é o gargalo? *(se sim, o problema é codec, e a resposta é H.264,
   não WebRTC — vai pra F9)*

**Se as três respostas forem "não", o WebRTC sai do plano.** Não é derrota: é
ter descoberto que o Tailscale já entregou o que ele traria, por um décimo do
custo. Se alguma for "sim", aí sim vale a reescrita — e ela será *muito* mais
fácil com o Core da F4 pronto do que seria na sua Fase 3 original.

~~⚠️ Se decidir por WebRTC, saiba antes: não existe implementação .NET de primeira
linha.~~ **Corrigido — eu estava errado.** Verificado na implementação: o
[SIPSorcery](https://github.com/sipsorcery-org/sipsorcery) está em **10.0.15**,
acompanhando o .NET 10, e já traz Concentus (Opus) como dependência. Fizemos
`RTCPeerConnection` ↔ `RTCPeerConnection` negociar Opus mono, conectar por
DTLS-SRTP e passar áudio íntegro. Não foi preciso interop nativo nem
MixedReality-WebRTC (essa sim, arquivada). O risco que eu apontei aqui não se
confirmou.

---

### F9 — Tela: rever codec 🟡

Só se o portão apontar pra cá. O codec delta atual é bom e adaptativo em dois
eixos; o que ele não tem é compensação de movimento. H.264 via Media Foundation
(encoder por hardware, sem FFmpeg no exe) rende ~5x sobre JPEG solto.

**Pronto quando:** a mesma cena com movimento cabe em 1/3 da banda com a mesma
nitidez, medido com o contador que já existe (`ScreenSender.KbPerSecond`).

---

### F10 — Clipes, polimento e distribuição 🟢

Clipes já funcionam (buffer rolante + hotkey global + AVI). O que falta é
acabamento: mic e áudio do sistema separados na trilha, overlay, tray, tema,
atalhos, som de notificação.

Distribuição: instalador de verdade (`.msi`/Squirrel), assinatura de código
(acaba com o SmartScreen), atualização automática e o onboarding do Tailscale
da F3 embutido.

---

## 4. Riscos do plano

| Risco | Onde | Mitigação |
|---|---|---|
| Tailscale vira dependência de terceiro | F3 | Malha STUN mantida como plano B (§1.2) |
| Auth exige backend (Cloud Function) | F5 | Decidir explicitamente na entrada da fase; é o único ponto que pode custar hospedagem |
| Refatoração da F4 quebra o que funciona | F4 | F1 antes, obrigatoriamente |
| WebRTC .NET sem opção madura | Portão | Protótipo antes de comprometer |
| Escopo do "Discord privado" crescer sem fim | F6+ | Cada fase tem critério de saída; sem ele, não fecha |
| Modelo Servidor→Canal multiplicar leituras | F6 | Presença agregada por servidor, não doc por peer |

---

## 5. Se você quiser manter a sua ordem original

Não é irracional — é uma ordem que privilegia chegar na arquitetura alvo por
inteiro em vez de colher ganhos no caminho. Se for essa a escolha, três coisas
não são negociáveis:

1. **F1 (harness) primeiro mesmo assim.** Refatorar rede sem teste, com 9.000
   linhas e nenhuma cobertura, não tem plano B.
2. **Não remover a malha UDP antiga quando o WebRTC de voz funcionar.** Ela
   ainda carrega tela, música, cinema e arquivos. Sua Fase 3 diz "só depois
   remover o networking UDP antigo" — leia isso como "remover o *caminho de voz*
   do networking antigo"; o resto sobrevive até a Fase 7.
3. **Autenticação antes de Servidores.** Não dá pra ter membros sem saber quem
   é quem.

E vale saber o preço: manter os dois stacks de rede vivos entre a Fase 3 e a
Fase 7 significa dois caminhos de mídia, dois pontos de falha e dois lugares
para consertar cada bug de rede — por várias fases seguidas.

---

## 6. Implementado: voz por WebRTC

Estado: **compila, caminho de mídia validado, não testado em campo.**

### Como ligar

Desligado por padrão. Em `%APPDATA%\Primicord\config.txt`:

```
webrtc=1
```

Vale só para a **voz**. Tela, música do DJ, cinema e presença continuam na malha
UDP — ela sobe sempre.

### O que entrou

| Arquivo | O quê |
|---|---|
| [VoiceTransport.cs](../VoiceTransport.cs) | `IVoiceTransport` — a costura. Duas implementações reais |
| [WebRtcSignaling.cs](../WebRtcSignaling.cs) | Troca de SDP e candidatos pelo Firestore |
| [WebRtcVoice.cs](../WebRtcVoice.cs) | `WebRtcVoiceMesh` — uma `RTCPeerConnection` por pessoa, Opus |
| [RoomSession.cs](../RoomSession.cs) | Passou a declarar `IVoiceTransport` (já tinha os três membros) |
| [VoiceEngine.cs](../VoiceEngine.cs) | `AttachSession` → `AttachTransport(voz, música)` |

Dependência nova: `SIPSorcery 10.0.15` (+ `Concentus 2.2.2`, que ele já traz).

### Decisões que valem saber

**Sinalização não-trickle.** O padrão manda cada candidato ICE assim que aparece,
o que pressupõe websocket. O nosso canal é Firestore REST com poll de 2 s —
trickle não adiantaria nada e multiplicaria escrita. Esperamos a coleta terminar
e publicamos SDP + candidatos numa escrita só, por lado. Custa 1-2 s a mais para
conectar e elimina uma classe inteira de corrida.

**Quem oferta é determinístico:** o `peerId` menor por `CompareOrdinal`. Os dois
lados calculam igual, sem combinar — então "glare" (os dois ofertando ao mesmo
tempo), que é o bug clássico de sinalização em malha, não pode acontecer.

**Opus mono, quadros de 20 ms.** O padrão WebRTC anuncia `opus/48000/2` porque
navegador espera estéreo; aqui os dois lados são sempre Primicord e o pipeline é
mono de ponta a ponta. Testado: negocia `OPUS/48000/1` sem reclamar. Os 20 ms (em
vez dos 10 ms da malha) pagam o cabeçalho 50×/s em vez de 100×/s.

### Medido

| | Payload | Cabeçalho | Total por par |
|---|---|---|---|
| Malha atual (PCM cru) | 768 kbps | 30 kbps | **798 kbps** |
| WebRTC, falando sem parar | 29,2 kbps | 20,0 kbps | **49,2 kbps** |
| WebRTC, call real (~50% fala) | 17,0 kbps | 10,2 kbps | **27,2 kbps** |

Sala de 6: de ~4 Mbps para ~250 kbps no pior caso. Vem junto, de graça, o que a
malha nunca teve: **DTLS-SRTP** (a voz ia em texto puro), **FEC em banda** e
**PLC** para perda de pacote.

### ⚠️ Bloqueador operacional

A sinalização escreve numa subcoleção nova: `pc_rooms/{sala}/signal/{par}`. As
rules do Firebase precisam liberá-la. Como as rules do `pc_rooms` **ainda não
foram publicadas** (pendência que o README já registrava), isso vira um passo só:
publicar as rules cobrindo `pc_rooms/{sala}/peers/**` e `pc_rooms/{sala}/signal/**`.
Sem isso o app cai no banner de permissão negada e a voz não conecta.

### O que ainda não está coberto

- **Sem TURN.** ICE sem relay esbarra no mesmo NAT simétrico dos dois lados que a
  malha esbarrava. O ganho aqui é codec e segurança, **não alcance**. Quem não
  conectava antes continua não conectando.
- **Não testado com mais de 2 pessoas** nem em rede real — só o caminho de mídia,
  em laboratório.
- A malha UDP continua de pé e continua sem criptografia para tela/música/cinema.

### O que testar em campo, nesta ordem

1. Dois PCs, `webrtc=1` nos dois. A barra de status deve ir de
   `voz WebRTC negociando (0/1)` para `voz WebRTC (Opus, 1/1)`.
2. Mudo, volume individual, medidor de nível e gravação de clipe — tudo isso
   passa pelo `VoiceEngine`, que não mudou, mas o caminho de entrada mudou.
3. Um terceiro entra. Deve virar `(2/2)` nos três.
4. Alguém sai e volta. O `epoch` deve forçar renegociação sozinho.
5. Compartilhar tela com a voz no WebRTC — os dois stacks vivos ao mesmo tempo é
   exatamente o cenário que eu apontei como caro na §1.1. Vale medir a subida
   total aqui.
