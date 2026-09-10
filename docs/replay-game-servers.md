# Transmissão, clipes e servidores abertos

## Tela

`ScreenSender` captura um monitor pela Desktop Duplication (DXGI) quando o driver
oferece esse caminho. Janelas usam `PrintWindow`, porque Desktop Duplication só
entrega o desktop inteiro. O protocolo envia blocos JPEG de 128 px, com orçamento
total dividido pelos espectadores; blocos que não couberam continuam pendentes para
o próximo ciclo. A taxa alvo pode ser 15, 30 ou 60 FPS e `Fps` é uma medição real,
não um contador de iterações ociosas.

O áudio é escolhido separadamente. Uma janela pode usar o process loopback daquela
árvore de processos; “Todo o PC” usa exclusão da árvore do Primicord; “Sem áudio”
é um estado explícito. Se o Windows não suportar o process loopback, a captura
falha com diagnóstico e não troca silenciosamente de fonte.

## Replay

`ClipRecorder` mantém vídeo JPEG e PCM em memória, limitados por janela e por 96 MiB
por padrão. O atalho apenas tira um snapshot curto e agenda uma única exportação em
um worker. Uma segunda tecla retorna `Busy`; o arquivo é escrito como `.partial` e
renomeado atomically ao terminar, para nunca abrir um clipe incompleto.

## Servidores

`GameServerDirectory` guarda somente endpoints adicionados pelo usuário em
`%APPDATA%\Primicord\game-servers.json`. `GameServerProbe` faz uma conexão TCP com
deadline; Minecraft Java recebe o handshake/status oficial. Outros jogos são
marcados apenas como `Reachable`, porque uma porta aberta não prova que há uma
partida ou vaga. Endereços Tailscale podem ser usados diretamente.
