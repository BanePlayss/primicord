# Referências e prompts de imagem — 11/09/2026

Geradas com ImageGen. Sem dependência de imagens remotas no aplicativo.
Mockup é referência de composição; a arte de acampamento é o único raster novo
embutido no executável. O mockup não é uma captura nem um compromisso de features.

## Referência de call
Saída: `docs/design/nitro-reference.png`.
Entrada: captura real anterior `call-1440.png`, modo de prévia local.

Prompt:
> Use case: ui-mockup. Create one polished full desktop screenshot design reference for Primicord, a native Windows gaming community app. Redesign the attached actual screenshot for premium Discord Nitro-like product UX while preserving its Primitivao orange caveman crest brand. Single dark theme, compact useful layout, realistic readable Portuguese UI. No server icon rail. Left sidebar 256px with crest and Primitivos da Nova Era community header, Acampamento, Membros, then collapsible Voz e atividades with Salas, Transmissões, Jam, Servidores, Texto with # geral # clipes # musica # off-topic, Salas de voz with Arena principal active and member names beneath. Footer of left sidebar: green voice connected strip, elegant small playing League of Legends card with gamepad icon, Transmitir and Sair buttons, then user avatar Bane plus mic headphones gear. Main central area ONE header Arena principal, subtitle 5 na call, no disabled stream controls or duplicate headings. Spacious but efficient 2 by 3 grid: five participant tiles Bane, Mohamed, Ricle, Vitinho, Angu, and sixth dashed invite tile Chamar a tribo. Tiles have rich subtle blue/purple/slate gradients, distinct circular initial avatars, Bane green speaking ring, small game icon and game name chips bottom left, name and mic state clear. Not neon, no blur-glow. Main actions violet, orange limited to crest. Right sidebar 240px shows readable participant names/status and Jam do Spotify as an attached bottom layer, empty state and Entrar na Jam, no fake player. Near black #101116 canvas, #191a22 sidebar, #242630 cards, #a99aff focus accent, 14px body typography, 12px secondary, 8-12px corner radii. Keep full window margins in frame. Faithful functional app layout not a marketing landing page; no giant mockup devices, no fake premium badges or subscriptions. One screen, high fidelity crisp typography.

Análise: leitura de esquerda para direita; contraste por níveis de superfície;
ações primárias no perfil; presença concentrada à direita. Descartamos a contagem
inventada de 1.284 membros, capas de jogos geradas e demais dados sem fonte.
O fallback de avatar ganha uma cor estável por nome, compartilhada com a lista
de membros. A grade continua responsiva, em vez de reproduzir medidas do bitmap.

## Arte do Acampamento
Saída: `Assets/camp-banner.png` (2172 × 724; 3:1).

Prompt:
> Use case: background-asset. Wide cinematic game community home banner illustration, 3:1 panoramic composition. Primitivao: prehistoric tribal camp inside monumental obsidian cavern, premium stylized 3D videogame art, not photoreal, low angular basalt pillars, tiny orange campfire and spear silhouettes on RIGHT THIRD, faint lavender moonlight, distant violet mountains visible in cavern opening, polished rich dark tones #171820 #292035. LEFT TWO THIRDS nearly empty flat dark charcoal with barely perceptible rock shadow, reserved for white UI title and buttons added in code. Moody dignified welcoming place for friends, no humans close up, no text, no lettering, no logos, no UI, no border, no watermark. Orange ember warmth restricted right, static scene not noisy particles. Strong clear shapes, beautiful restrained art direction.

Análise: fundo escuro à esquerda mantém o título legível; fogueira e abertura da
caverna à direita preservam a identidade. Sobreposição escura no código protege
o contraste, sem filtros ou animação sobre o vídeo. Nenhum texto fica gravado no asset.
