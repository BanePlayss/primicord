# PRIMICORD

O app de comunidade dos primitivos — cliente nativo de Windows com uma interface
inspirada na organização do Discord e identidade visual do escudo Primitivão.
Versão publicada: **0.9.2**.

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
`firestore.rules` do repo `primitivao`) no Firebase Console. Sem isso o app não
lista nem cria sala.
