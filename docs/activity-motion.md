# Atividade e movimento — próxima entrega pré-1.0

Referências consultadas: [Magic UI](https://github.com/magicuidesign/magicui)
e [Motion](https://github.com/motiondivision/motion), ambos MIT.
[Border Beam](https://magicui.design/docs/components/border-beam) e
[Shine Border](https://magicui.design/docs/components/shine-border) orientaram a
borda iluminada e o brilho breve. A implementação C#/GDI+ é original: nenhum
componente React/JS, código ou asset desses projetos foi incorporado.

## Uso

O cartão acima do perfil mostra o jogo em execução, ícone local e tempo desde
que a atividade foi reconhecida. Clique nele (ou use Tab + Enter) para detectar
jogos conhecidos, selecionar outro aplicativo aberto, escrever um nome ou ocultar
a atividade. Configuração de atividade vale para a sessão atual do aplicativo.

A detecção consulta processos a cada 10 segundos fora da thread de desenho.
Reconhece uma lista explícita, como League of Legends, VALORANT, CS2, Rust e
Minecraft. Java só é identificado como Minecraft quando sua janela indica o jogo.
Launchers não contam como jogo. Aplicativos não reconhecidos podem ser escolhidos
manualmente. A atividade selecionada é limpa quando o processo fecha.

O ícone vem do executável local. Se indisponível (incluindo atividade digitada),
aparece um controle de videogame. O ícone local não é enviado aos outros PCs:
eles recebem o nome pelo campo de atividade que já existe na sala, com um selo
de controle de videogame no tile. Não há catálogo de capas ou integração oficial
com Discord. O nome só é compartilhado enquanto estiver na sala de voz.

## Animação

Cartão com gradiente violeta, borda que responde ao hover/foco e brilho único de
650 ms ao trocar de atividade. Botões de perfil com hover de 180 ms e pressão com
retorno suave de 240 ms. O relógio de animação já existente para quando termina;
respeita animações desativadas no Windows e o modo de movimento reduzido.
Não há efeitos de partículas ou redesenho contínuo do palco.

Escopo desta alteração: UX e atividade local; não altera formato da presença,
codec, captura de tela nem numeração de releases. A versão 1.0 continua dependente
de autorização explícita do usuário.
