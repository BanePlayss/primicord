# Revisão de UX — Obsidiana

## Implementado
- `MainForm.cs:366`: cabeçalho central independente da marca; sem título global
  redundante sobre call/chat; cabeçalho da comunidade não sobrepõe ícones.
- `RailSection.cs:6`: expandir/recolher por clique, Enter, Espaço e setas;
  criação de sala é um botão separado. Restauração de foco após reconstruir o rail.
- `RailScrollHost.cs:7`: rolagem escura por roda, arraste, página e teclado.
  Foco em um canal fora do viewport traz o canal para a área visível.
- `MainForm.cs:639`: ordem de Tab acompanha a ordem visual; Membros é um toggle,
  não uma segunda seleção de rota. Recolher voz não desconecta a sala.
- `PrimicordUi.cs:108`: rótulos sem uppercase forçado, foco visível, nomes
  acessíveis atualizados e botões com raio consistente.
- `WatchView.cs:33`: cabeçalho único; ações de assistir escondidas sem transmissão.
- `PeerTile.cs:162`: cores de avatar estáveis, superfícies discretas, fala e
  microfone com sinais além de cor; atividade respeita os dados reais da sessão.
- `JamPanel.cs:20`: título curto, estado vazio legível e pessoas no mesmo painel;
  sem player/fila simulados. Reprodução continua sob responsabilidade do Spotify.
- `DashboardView.cs:88`: arte estática, título legível em notebook, ações preservadas;
  cartões menores permitem acessar as salas sem rolar um cartão pela metade.
- `ActionBar.cs`: legendas de replay não invadem o botão vizinho; alvo de FPS
  identificado como alvo, sem apresentá-lo como taxa medida.

## Verificações
Build Release sem avisos/erros. Harness visual cobre 18 combinações: Acampamento,
call, Jam, transmissão, duas telas e fullscreen em 1280×720, 1440×900, 1920×1080.
Testes adicionais: seleção/troca de transmissão, retorno de fullscreen, limites
de palco/perfil/ações, teclado das categorias, limites de rolagem e seleção única.
O harness encerra com erro e escreve diagnóstico em vez de deixar uma janela
aberta quando uma asserção ou exceção interrompe a renderização.
Teste existente de presença: entradas obsoletas, futuras e duplicadas filtradas.

Capturas em `call.png`, `acampamento.png` e `transmissao.png` são do aplicativo
real em prévia isolada, com dados de teste. `nitro-reference.png` é apenas conceito
gerado, não screenshot de implementação.

## Limites desta revisão
Não mede transmissão entre dois PCs, conectividade Firestore/Tailscale, captura
real, FPS sustentado ou acessibilidade com leitor de tela. Não substitui esses
testes de integração. Nenhuma mudança no protocolo de rede/encoder foi realizada.
Não houve publicação de release nem instalação sobre o aplicativo em uso.
Permanece antes da 1.0, conforme instrução do usuário.

## Referências usadas
- https://github.com/Leonxlnx/taste-skill
- https://github.com/Leonxlnx/taste-skill/tree/main/skills/image-to-code-skill
- https://github.com/vercel-labs/agent-skills/tree/main/skills/web-design-guidelines
- https://raw.githubusercontent.com/vercel-labs/web-interface-guidelines/main/command.md
- https://github.com/voltagent/awesome-design-md

As referências web foram adaptadas para WinForms. Nenhum pacote React, runtime
web ou código de animação remoto foi adicionado ao aplicativo.
