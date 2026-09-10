# PRIMICORD

O app de comunidade dos primitivos — cliente nativo de Windows com uma interface
inspirada na organização do Discord e identidade visual do escudo Primitivão.
Versão publicada: **0.8.1**.

## O que já funciona

- canais de texto (`geral`, `clipes`, `musica` e `off-topic`), busca local e DMs;
- presença online, painel de membros e participantes dentro de cada sala de voz;
- voz P2P em grupo, mutar, ensurdecer e volume individual;
- compartilhamento de tela/janela com seleção independente de áudio (sem fallback
  silencioso), Desktop Duplication pela GPU para monitores e perfis de 15/30/60 FPS;
- buffer rolante limitado e exportação assíncrona de clipes por atalho global;
- Sala DJ paralela à voz: só quem entra ouve a música, sem sair da call;
- um participante transmite Spotify, YouTube ou qualquer áudio do sistema;
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
- [x] Sala DJ seletiva, paralela à chamada, e Cinema sincronizado
- [x] shell visual com canais, chat, palco, membros e dashboard em uma comunidade única
- [x] Tailscale-only para salas normais e diagnóstico de conexão
- [x] cadastro local e sondagem de servidores abertos
- [ ] paridade completa: cargos/permissões, anexos, reações, threads, bots e moderação

**Passo manual pendente:** publicar as rules do `pc_rooms` (arquivo
`firestore.rules` do repo `primitivao`) no Firebase Console. Sem isso o app não
lista nem cria sala.
