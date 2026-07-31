# PRIMICORD

O Discord dos primitivos — app nativo de Windows pra sala de voz em grupo,
compartilhar tela e tirar clipe dos últimos segundos.

Irmão do [CherrySpy](https://github.com/BanePlayss/duovoz) (que é 1-pra-1 e só na
LAN). O Primicord é feito pra **N pessoas pela internet**.

## Como funciona

- **Voz**: malha UDP ponto-a-ponto — cada um manda direto pra cada outro. Não tem
  servidor no meio, ninguém paga hospedagem e nenhum áudio passa por terceiros.
- **Encontro**: o Firestore do projeto `primitivao` (coleção `pc_rooms`) guarda só
  a lista de salas, quem está em cada uma e o IP:porta público de cada um.
- **NAT**: cada cliente descobre seu endereço público por STUN e os dois lados furam
  o NAT mandando pacotes ao mesmo tempo (hole punching).
- **Áudio**: PCM 48kHz mono 16-bit, frames de 10ms, jitter buffer por pessoa,
  mixados com NAudio.

### Limite conhecido

Se os **dois** lados estiverem atrás de NAT simétrico (comum em 4G e alguns
provedores), o furo não acontece e o par não conecta — só um relay (TURN)
resolveria. O app avisa na tela em vez de ficar mudo sem explicação.

### Eco

Não tem cancelamento de eco acústico. **Todo mundo de fone.** Quem usar caixa de
som vai devolver a voz dos outros pelo microfone.

## Rodar

Só abrir o `Primicord.exe` — é **um arquivo só** e não precisa de .NET instalado
na máquina (self-contained, ~56MB). Na primeira vez o Windows mostra o aviso do
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

- [x] Fase 1 — salas de voz em grupo pela internet
- [ ] Fase 2 — compartilhar tela
- [ ] Fase 3 — clipes (buffer rolante + hotkey global)

**Passo manual pendente:** publicar as rules do `pc_rooms` (arquivo
`firestore.rules` do repo `primitivao`) no Firebase Console. Sem isso o app não
lista nem cria sala.
