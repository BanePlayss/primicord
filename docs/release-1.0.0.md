# Primicord 1.0 — a tribo reunida

## Novidades desta versão

- **Transmissão sem a grade de faixas pretas:** remontagem dos blocos em pixels
  exatos, independente do DPI gravado pelo transmissor. Compatível com os
  transmissores existentes e com blocos parciais nas bordas da imagem.
- **Texto mais nítido:** suavização consistente, legendas maiores e menos pesadas,
  títulos e botões mais legíveis e posições inteiras durante animações de texto.
- Mantém a UX Obsidiana: Acampamento, categorias recolhíveis, atividades de jogos,
  participantes, Jam do Spotify paralela à voz, clipes e seleção de transmissões.

## Instalar ou atualizar

Baixe **Primicord-win-Setup.exe**. Quem já instalou pelo Setup pode usar
**Configurações → Procurar atualização**. Cópias portáteis ou instalações antigas
sem atualizador funcional devem executar o Setup para habilitar esse canal.

App e pacote agora usam **1.0.0**. A versão pública anterior, 0.9.9.1,
usava o pacote interno 0.9.10; o canal mantém a mesma identidade e reconhece
a 1.0.0 como atualização. Quem assiste precisa atualizar para receber a
correção de remontagem da imagem.

## Validação e limites

- 96 combinações sintéticas: oito resoluções (até 4K, ultrawide e vertical),
  seis valores de DPI, PNG e JPEG; comparação de cada pixel decodificado.
- 20 testes de suavização de fontes e 18 layouts com navegação, teclado,
  troca de transmissões e restauração de tela cheia.
- Teste de filtragem de presença e descoberta de atualização para versões antigas.

Os testes locais não substituem uma chamada entre dois PCs via Tailscale.
Esta versão não promete FPS mínimo constante, compressão JPEG sem perda nem
paridade completa com o Discord. O instalador ainda não possui assinatura digital.
