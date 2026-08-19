# PRIMICORD

O Discord dos primitivos — app nativo de Windows pra sala de voz em grupo,
compartilhar tela e tirar clipe dos últimos segundos.

## 0.6.17 — o Primicord também aparece na transmissão

- Remove a exclusão da janela do Primicord durante o compartilhamento.
- Se o app estiver visível no monitor ou na janela escolhida, todos o verão no
  quadro normalmente, inclusive na prévia local.
- Ao compartilhar o próprio monitor com o Primicord aberto, o efeito de espelho
  passa a ser esperado porque a prévia está filmando a si mesma.

## 0.6.16 — prévia, áudio isolado e volume por pessoa

- Quem compartilha passa a ver a própria tela ao vivo no palco.
- A janela do Primicord é excluída da captura para evitar o espelho infinito.
- O process loopback usa o ponteiro COM correto e foi validado no Windows real.
- Se o isolamento do áudio falhar, a tela continua sem áudio em vez de devolver
  as vozes para a sala e criar eco.
- Botão direito no card de uma pessoa abre um volume local de 0% a 200%; cada PC
  guarda seu próprio valor e não afeta os demais participantes.
- A tela de login e as configurações agora têm **Entrar no grupo com código**,
  cobrindo também quem recebeu o app por atualização diferencial.

## 0.6.15 — um Setup e um código curto

- Todos baixam o mesmo `Primicord-win-Setup.exe`; não existe mais um executável
  diferente para cada grupo.
- O Setup pede o código compartilhado e configura Tailscale, salas e réplicas
  sem abrir cadastro ou painel de rede.
- Um ativador público independente do site troca o código por uma credencial do
  Tailscale. Firestore não participa da instalação.
- O modo recomendado cria uma auth key de uso único, válida por 10 minutos; os
  segredos OAuth ficam cifrados no provedor e não entram no app ou no GitHub.
- Cinco tentativas por minuto e respostas sem cache reduzem abuso do código curto.

## 0.6.14 — salas sem dono físico

- Todo PC com Primicord mantém uma réplica SQLite das salas, presença e chat.
- Os participantes online são descobertos automaticamente pelo Tailscale.
- Leituras usam um coordenador determinístico e migram para o próximo se ele cair.
- Escritas são espelhadas nas réplicas online; o áudio continua ponto a ponto.
- Snapshots fazem um PC que voltou receber o estado atual do grupo.
- Exclusões viram tombstones para uma réplica antiga não ressuscitar salas apagadas.

## 0.6.13 — instalador privado do grupo

- O administrador gera um único Setup protegido por senha para o grupo inteiro.
- A auth key reutilizável fica cifrada e nunca é publicada no GitHub ou no `config.txt`.
- O Setup instala o Tailscale, entra na tailnet e configura o mini servidor sozinho.
- A chave passa ao Tailscale por arquivo temporário, sem aparecer na linha de comando.
- Quem já está conectado à tailnet correta não consome a chave novamente.

## 0.6.12 — chamada em grade como o Discord

- Remove a arena espacial, o arraste, o zoom e as posições livres dos avatares.
- Organiza os participantes automaticamente em cards responsivos.
- Câmera preenche o card; voz ativa ganha contorno verde e status no rodapé.
- Durante tela compartilhada, o vídeo ocupa o palco e os usuários viram miniaturas.

## 0.6.11 — compartilhamento sem X vermelho

- Sincroniza a pintura do palco com os blocos recebidos da tela compartilhada.
- Impede o GDI+ de ler o bitmap enquanto a rede ainda está escrevendo nele.
- Mantém o quadro estável sem cópias grandes a cada atualização.

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
- **Encontro**: cada Primicord guarda uma réplica de salas, presença, chat e
  sinalização em SQLite. O primeiro PC saudável coordena leituras; escritas vão
  para todos e outro assume automaticamente se ele cair.
- **NAT**: o endereço da tailnet é o primeiro candidato. O Tailscale escolhe a rota
  direta ou relay; STUN e hole punching continuam como plano B.
- **Áudio**: 48kHz mono, frames de 10ms, jitter buffer por pessoa, mixados com
  NAudio. Na malha vai PCM cru (798 kbps por par); com `webrtc=1` vai Opus por
  RTP/SRTP (49 kbps por par, e criptografado).

### Configurar o grupo

1. O administrador configura uma vez o ativador descrito em
   [`activation-worker/README.md`](activation-worker/README.md). Ele fica separado
   do site e guarda o segredo do Tailscale no cofre do provedor.
2. Todos baixam o mesmo `Primicord-win-Setup.exe` da release mais recente.
3. Cada participante executa o Setup e cola o código recebido do administrador.
4. O instalador baixa o Tailscale, entra na tailnet e liga a réplica local sozinho.

O código não fica salvo no PC. A credencial do Tailscale passa por arquivo
temporário apagado ao fim da instalação. Setups privados da 0.6.13 continuam
aceitos somente para compatibilidade.

Cada PC guarda sua réplica em `%LOCALAPPDATA%\PrimicordServer\primicord.db`.
Áudio, câmera e tela não passam por ela: continuam ponto a ponto.

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
- [x] Chamada em grade responsiva com palco de tela e miniaturas
- [x] Réplicas SQLite com troca automática de coordenador

Ver [docs/PLANO-DE-MIGRACAO.md](docs/PLANO-DE-MIGRACAO.md) pro que vem depois.

**Compatibilidade:** quem ainda não entrou na tailnet depende das rules de
`pc_rooms`, `peers/**` e `signal/**` no Firebase. Na tailnet, salas e sinalização
seguem pelas réplicas SQLite e não exigem essas regras para o caminho principal.
