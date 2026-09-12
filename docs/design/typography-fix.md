# Correção de nitidez tipográfica

O Hero e RoomCard configuravam SmoothingMode para formas, mas deixavam
DrawString em TextRenderingHint.SystemDefault. O restante da interface misturava
ClearType, GDI e GDI+; legendas de 8.5 pt em negrito acentuavam a diferença.

Correção posterior à 0.9.9.1, incluída na versão 1.0.0:
- `Pv.PrepareText` configura AntiAliasGridFit para texto nos controles desenhados.
- Hero, cartões, rail, participantes, perfil, convite e ações compartilham essa
  configuração; não são alteradas as preferências de fonte do Windows.
- Translações animadas de texto arredondadas a pixels inteiros.
- Legendas Segoe UI 9 pt regular; botões Segoe UI Semibold 9.5 pt; títulos Semibold.
- A correção tipográfica não altera layout, protocolo, captura ou encoder.

Testes: 18 layouts do app, mais rasterização dos cinco estilos em 96/120/144/192 DPI,
verificando pixels intermediários de suavização e ausência de bordas coloridas em
texto branco sobre preto. Isso não testa mudanças entre monitores com escalas
diferentes: o aplicativo continua SystemAware, não PerMonitorV2.

Referência técnica: [Microsoft — TextRenderingHint](https://learn.microsoft.com/en-us/dotnet/api/system.drawing.text.textrenderinghint?view=windowsdesktop-10.0).
