# Publicar as rules do Firestore

O Primicord usa o projeto Firebase `primitivao` apenas como ponto de encontro:
salas, presença e sinalização. Áudio e vídeo não passam pelo Firestore.

## Corrigir o erro de permissão

A regra fonte fica no repositório `primitivao`, em
`D:\projects\primitivao\firestore.rules`. A revisão atual aceita os 13 campos
de presença usados pelo Primicord 0.9.8/0.9.9. Na máquina que estiver logada na
conta Firebase do projeto, execute:

```powershell
firebase login
cd D:\projects\primitivao
firebase deploy --only firestore:rules
```

O comando precisa terminar com `Deploy complete!`. Depois, feche e abra o
Primicord. O 0.9.9 também tenta automaticamente o formato legado de 12 campos
enquanto uma instalação ainda estiver usando a regra antiga; isso evita que a
presença fique presa em “sem conexão” durante a transição.

O projeto ainda não usa Firebase Auth. Por isso as regras atuais são públicas
para as coleções efêmeras do Primicord; não use as DMs para dados secretos.
