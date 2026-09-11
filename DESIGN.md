# Primicord — Obsidiana / comunidade premium

## Design Read
Redesign de um aplicativo nativo de comunidade gamer: familiaridade do Discord,
densidade de ferramenta de comunicação e identidade Primitivão. Não é uma landing
page nem uma implementação do Discord. Sem assinatura ou selo Nitro fictício.

## Escopo e referências
- Taste Skill: aplicada ao Acampamento e à revisão de hierarquia. A própria skill
  exclui interfaces colaborativas densas; não determina a arquitetura da call.
- Image-to-Code: referência visual gerada antes do código; análise e adaptação
  para WinForms, sem migrar a aplicação para React.
- Web Design Guidelines: equivalentes nativos de foco, teclado, nomes acessíveis,
  contraste, movimento reduzido e conteúdo truncado com segurança.
- awesome-design-md: Linear (níveis de superfície, tipografia, bordas) e Slack
  (separação entre conteúdo e decoração). São análises de marketing, não contratos
  de produto: suas cores, fontes proprietárias e regras específicas não são copiadas.

Fontes: https://github.com/Leonxlnx/taste-skill,
https://github.com/vercel-labs/agent-skills/tree/main/skills/web-design-guidelines,
https://github.com/voltagent/awesome-design-md.

## Parâmetros
Call: DESIGN_VARIANCE 3, MOTION_INTENSITY 3, VISUAL_DENSITY 7.
Acampamento: DESIGN_VARIANCE 5, MOTION_INTENSITY 4, VISUAL_DENSITY 4.
Tema escuro fixo nesta revisão; preferências existentes de cor do usuário preservadas.

## Tokens e composição
- Canvas #101116, rail #191A22, superfície #242630, hover #30323E.
- Texto #F2F0EC, secundário #B6B8C5. Violeta #A99AFF em foco/destaques;
  laranja permanece na marca e no tema personalizado. Verde indica voz/online,
  vermelho indica mute/erro, nunca dependem só da cor para comunicar estado.
- Segoe UI 10 pt corpo, 8.5 pt legenda; títulos de sala 15 pt sem tracking.
- Espaçamento 4/8/12/16/24; raios 8 para botões, 12 para cartões.
- Rail com ícone e rótulo, categorias realmente recolhíveis. Sem barra de servidores.
- Um título por superfície; botões de assistir só existem com transmissão.
- Participantes à direita; Spotify Jam é uma camada da mesma call, não outra sala.
- Jogos mostram apenas presença recebida ou detectada; ícone genérico é fallback
  explícito, não uma capa de jogo inventada. Nenhum dado gerado vira presença real.
- Arte apenas no Acampamento; nenhuma textura ou animação decorativa sobre vídeo.
- Animações nativas finitas, via relógio compartilhado UiMotion; sem novo runtime JS.

## Referência gerada e desvios intencionais
`docs/design/nitro-reference.png` é um conceito, NÃO captura do aplicativo.
O gerador inventou contagem de membros e capas de jogos; esses elementos foram
rejeitados. O projeto usa a própria marca, avatares reais quando disponíveis,
iniciais coloridas e nomes de jogos da presença. A grade adapta colunas ao espaço.
`Assets/camp-banner.png` é arte decorativa original, embutida no executável.
Prompts e proveniência em `docs/design/prompts.md`.

## Invariantes
Não alterar rede, identidade de sala, Firestore, captura ou encoder nesta revisão.
Não lançar 1.0 sem autorização. Não confundir build/teste local com release instalada.
Validar Acampamento, call, Jam, transmissão, duas telas e fullscreen em 3 tamanhos;
exercitar recolhimento pelo teclado e restauração dos controles de transmissão.
