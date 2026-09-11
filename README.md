# PRIMICORD

O app de comunidade dos primitivos — cliente nativo de Windows com uma interface
inspirada na organização do Discord e identidade visual do escudo Primitivão.
Versão publicada: **0.9.9.1**.

## 0.9.9.1 — Obsidiana: nova UX da tribo

- Acampamento com arte original, cartões compactos e ações mais legíveis.
- Navegação com categorias recolhíveis por clique e teclado, rolagem escura e foco visível.
- Call sem cabeçalhos duplicados ou controles de assistir quando não há transmissão.
- Avatares com cores estáveis, atividade de jogo e Jam do Spotify integrada ao painel de participantes.
- Controles de replay com rótulos claros e indicador de FPS identificado como alvo configurado.
- Mantém o canal de atualização e a identidade do pacote das versões anteriores.

Validação: 18 layouts em três tamanhos, navegação de categorias pelo teclado,
troca de transmissões, restauração de tela cheia e teste de filtragem de presença.
Esta revisão não altera captura/encoder e não promete melhoria de FPS entre PCs.

[Baixar instalador](https://github.com/BanePlayss/primicord-releases/releases/download/v0.9.9.1/Primicord-win-Setup.exe).
Quem já instalou pelo Setup pode usar **Configurações → Procurar atualização**.
O pacote Velopack usa 0.9.10 internamente (SemVer de três partes); o nome público
e a versão exibida pelo Primicord continuam 0.9.9.1.

## 0.9.8 — presença confiável e movimento do palco

- A presença de uma sala agora usa uma concessão de 15 segundos baseada no horário do servidor, remove entradas antigas e consolida identidades pelo nick da conta. Duas sessões do mesmo nick não aparecem mais como dois participantes.
- A entrada verifica o documento da sala, sincroniza os participantes antes de exibir a call e encerra o heartbeat anterior antes de limpar a presença. Se a rede falhar, a última lista válida fica visível com o estado de reconexão.
- Os endpoints Tailscale são atualizados sem apagar uma conexão válida a cada polling; uma mudança de endereço refaz o furo automaticamente.
- A interface usa transições curtas com easing cúbico, spring no clique, entrada escalonada dos cards, indicador animado de sala ativa e halo de fala. A animação segue o relógio do WinForms e é desativada na prévia automatizada.

O movimento visual segue a ideia do [Anime.js](https://animejs.com/): timelines curtas, easing, stagger e spring, traduzidos para os controles nativos do Primicord sem adicionar uma dependência web.

## 0.9.7 — reduzir o custo real da transmissão

- Captura DXGI em resolução nativa copia direto para o bitmap, eliminando a passagem intermediária por DIB/BitBlt.
- Os blocos são comprimidos diretamente dos pixels capturados, sem um DrawImage por bloco; formato de rede compatível com as versões anteriores.
- Compressão do clipe em uma tarefa separada, com somente um quadro pendente, limitada ao ritmo de 30 FPS do buffer.
- Prévia local de até 30 FPS (antes: 5 FPS), sem fila de imagens atrasadas.
- Envio alterna a região inicial da tela: movimento no topo não impede os blocos inferiores de serem atualizados.
- Contadores distinguem capturas reais, envios de pacotes de blocos e ciclos sem mudança. Diagnóstico de tempo por etapa no log a cada 5 segundos.

Validação reproduzível: `dotnet run --project Tests/ScreenPerf -c Release` abre uma cena de movimento e testa captura, compressão e remontagem via UDP local, com e sem clipe. `dotnet run --project Tests/Presence -c Release` verifica o lease de 15 segundos, o descarte de relógio futuro e a deduplicação de duas sessões com o mesmo nick. Imagens não são salvas nem enviadas para outros computadores. No monitor 1080p desta máquina, o recebimento passou de 22–25 para cerca de 40 atualizações de blocos/s e a prévia passou de 5 para 30 FPS. Isso não equivale a 40 quadros completos/s: a cobertura por atualização depende da banda. Não foi medido desempenho entre dois computadores via Tailscale.

## 0.9.6 — pacing estável de transmissão

- o loop usa pacing de alta precisão para não deixar o timer do Windows transformar
  60 FPS em aproximadamente 30 FPS;
- o alvo mínimo passou a ser 30 FPS; configurações antigas com 15 FPS são migradas
  para 30 FPS;
- o indicador mostra FPS observados e o alvo configurado no painel da transmissão.

## 0.9.5 — assistir, comparar e ampliar transmissões

- clique no palco ou use Tela cheia/F11 para ampliar mantendo os participantes
  visíveis; Esc ou Voltar restaura a navegação sem sair da call;
- o menu Assistir lista as transmissões ativas, incluindo a sua própria prévia;
- + Outra tela abre uma segunda transmissão: lado a lado em áreas largas e
  empilhadas em áreas estreitas. Fechar 2ª tela fecha apenas essa visualização;
- clique no participante com selo AO VIVO para focar a tela; Ctrl+clique adiciona
  a segunda. Trocar a tela assistida não encerra a sua transmissão;
- voltar aos canais preserva a voz, a transmissão publicada e a seleção de telas;
- ao terminar uma transmissão remota, a visualização deixa de focar uma fonte
  inativa. O segundo painel é removido quando sua fonte termina.

Validação local: 18 layouts de prévia com testes de seleção, troca entre telas,
retorno à própria prévia, navegação, fim de transmissão e restauração de tela cheia.
Esses testes não substituem uma call real entre PCs para avaliar FPS e áudio.

## 0.9.4 — UX de canais e call

- canais de texto e salas de voz acessíveis diretamente pela lateral, com a sala
  conectada destacada e seus participantes recuados abaixo;
- barra de voz conectada clicável para voltar à call; perfil compacto com microfone,
  fone, configurações, transmitir e sair no rodapé;
- cartões da call arredondados, avatar central, anel verde de fala, nome no canto,
  atividade e selo da Jam; convite ocupa a última posição da grade;
- superfícies neutras escuras e destaque laranja da identidade Primitivão;
- prévia local ampliada e miniaturas dos participantes visíveis durante a transmissão;
- controles do perfil acessíveis por teclado e motivo explícito quando transmitir
  está indisponível. A Jam continua como camada inferior do painel direito.

A verificação visual cobre 12 layouts em 1280×720, 1440×900 e 1920×1080,
incluindo a separação entre palco, participantes, navegação e controles do perfil.

## 0.9.3 — transmissão contínua e recuperação de pacotes

- cada quadro de tela agora leva uma paridade XOR: um fragmento UDP perdido é
  reconstruído no receptor sem esperar a próxima atualização completa;
- a ressincronização de blocos foi acelerada para remover artefatos em menos de
  meio segundo numa tela 1080p;
- o perfil Máxima passa a ser descrito como 1080p/60 adaptativo, com orçamento
  de 32 Mbps e recuperação de pacotes;
- a prévia local e os indicadores de FPS, resolução, qualidade e bitrate da
  0.9.2 continuam disponíveis.

> Nota técnica: a camada atual continua sendo um transporte P2P de tiles com
> PNG/JPEG. O Discord usa codecs de vídeo H.264/VP8/VP9/AV1 e SFU/WebRTC; para
> chegar a essa equivalência literal será necessária uma etapa futura de codec
> nativo, além destas correções de transporte.

## 0.9.2 — transmissão nítida e prévia local

- modo de nitidez máxima mantém a resolução nativa e JPEG 100 em movimento;
- blocos estáticos usam PNG sem perda para texto, HUD e janelas paradas;
- o orçamento padrão de transmissão prioriza 32 Mbps quando selecionado;
- quem transmite vê uma prévia local da fonte no palco, sem enviar esse quadro de volta pela rede;
- a prévia visual inclui um quadro de teste para garantir que o palco não fique vazio ao iniciar.

## 0.9.1 — atualizações pelo próprio app

- configurações agora mostram a versão instalada e o canal público;
- o botão procura primeiro e só baixa depois da confirmação;
- download mostra progresso e o Primicord reinicia sozinho após instalar;
- builds instalados pelo Setup continuam no mesmo canal de atualização das versões antigas.

## 0.9.0 — UX da call

- avisos de conexão e atalho somem sozinhos e podem ser dispensados com um clique;
- transmitir fica desabilitado fora de uma sala, com o estado explicado no perfil;
- a barra de transmissão informa ao vivo se a tela, o FPS e o buffer de clipes estão prontos;
- a Jam mostra cada participante em uma linha, com contagem e indicação clara de que fila/play
  continuam no Spotify;
- rail, ações de transmissão e botões principais aceitam teclado e expõem rótulos de acessibilidade;
- a prévia visual valida também os estados de perfil e transmissão, além da composição da sala.

## O que já funciona

- canais de texto (`geral`, `clipes`, `musica` e `off-topic`), busca local e DMs;
- presença online, painel de membros e participantes dentro de cada sala de voz;
- voz P2P em grupo, mutar, ensurdecer e volume individual;
- compartilhamento de tela/janela com seleção independente de áudio (sem fallback
  silencioso), Desktop Duplication pela GPU para monitores e perfis de 15/30/60 FPS;
- buffer rolante limitado e exportação assíncrona de clipes por atalho global;
- Jam do Spotify paralela à voz: o Spotify controla a fila e o play, enquanto o
  Primicord mostra o subgrupo confirmado na Jam sem criar uma segunda sala;
- sessão Cinema sincronizada;
- troca de dispositivos durante a call, bandeja do Windows e tema do usuário;
- painel de servidores abertos cadastrados pelo usuário, com sonda real de Minecraft
  Java ou alcance TCP (sem descoberta automática nem dados inventados).

Irmão do [CherrySpy](https://github.com/BanePlayss/duovoz) (que é 1-pra-1 e só na
LAN). O Primicord é feito pra **N pessoas pela internet**.

## Como funciona

- **Voz**: malha UDP ponto-a-ponto — cada um manda direto pra cada outro. Não tem
  servidor no meio, ninguém paga hospedagem e nenhum áudio passa por terceiros.
- **Encontro**: o Firestore do projeto `primitivao` (coleção `pc_rooms`) guarda só
  a lista de salas, presença e candidatos de conexão.
- **Rede**: salas normais exigem um endereço IPv4 do **Tailscale** (100.64.0.0/10),
  publicam somente candidatos dessa rede e não fazem fallback para a internet. O
  modo `StartNetworkOnlyAsync` existe apenas para testes locais.
- **Áudio**: PCM 48kHz mono 16-bit, frames de 10ms, jitter buffer por pessoa,
  mixados com NAudio.

### Prévia visual

Para revisar o shell sem login, Firestore, microfone ou Tailscale:

```powershell
./bin/Debug/net10.0-windows10.0.19041.0/Primicord.exe --preview
```

O modo é explicitamente rotulado como prévia e não publica presença nem usa dados
de servidores reais.

A prévia visual da 0.9.0 também pode renderizar Acampamento, call sem
transmissão, Jam confirmada e transmissão ativa em 1440x900 e 1280x720 usando
o argumento --preview --render-preview <pasta>.

### Eco

Não tem cancelamento de eco acústico. **Todo mundo de fone.** Quem usar caixa de
som vai devolver a voz dos outros pelo microfone.

## Rodar

Só abrir o `Primicord.exe` — é **um arquivo só** e não precisa de .NET instalado
na máquina (self-contained, inclui o motor de vídeo, ~232MB). Na primeira vez o Windows mostra o aviso do
SmartScreen ("Mais informações" → "Executar assim mesmo"), porque o exe não é
assinado.

Pra gerar o exe de novo depois de mexer no código:

```powershell
./build.ps1
```

Ou rodar direto do código:

```
dotnet run --project Primicord.csproj
```

## Estado

- [x] salas de voz em grupo pela internet
- [x] canais de texto, DMs e presença
- [x] compartilhar janela/monitor com áudio de aplicativo escolhido e perfis de FPS
- [x] clipes (buffer rolante + hotkey global)
- [x] Jam do Spotify seletiva, paralela à chamada, e Cinema sincronizado
- [x] shell visual com canais, chat, palco, membros e dashboard em uma comunidade única
- [x] Tailscale-only para salas normais e diagnóstico de conexão
- [x] cadastro local e sondagem de servidores abertos
- [ ] paridade completa: cargos/permissões, anexos, reações, threads, bots e moderação

**Passo manual pendente:** publicar as rules do `pc_rooms` (arquivo
`firestore.rules` do repo `primitivao`) no Firebase Console. O Primicord 0.9.9
também faz fallback para a regra antiga de 12 campos, mas o deploy é necessário
para liberar criação, presença completa e sinalização WebRTC. Veja o passo a
passo em [docs/firestore-rules.md](docs/firestore-rules.md).
