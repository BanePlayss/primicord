# Primicord activation worker

Serviço público pequeno e independente do site. O Setup troca o código curto do
grupo por uma credencial do Tailscale e pelo endereço inicial das réplicas.

Segredos obrigatórios no Cloudflare:

- `GROUP_CODE_HASH`: SHA-256 hexadecimal do código normalizado;
- `PRIMICORD_SERVER_URL`: endereço `http://100.x.y.z:8765` de uma réplica inicial;
- `TAILSCALE_AUTH_KEY`: chave reutilizável, para o modo simples; ou
- `TAILSCALE_OAUTH_CLIENT_ID`, `TAILSCALE_OAUTH_CLIENT_SECRET` e `TAILSCALE_TAG`:
  o modo recomendado, que cria uma auth key de uso único e validade de 10 minutos.

O cliente OAuth precisa do escopo `auth_keys` e da tag configurada. Nenhum desses
segredos entra no repositório, no Setup ou no Firestore.

```powershell
npm ci
npm run check
npx wrangler secret put GROUP_CODE_HASH
npx wrangler secret put PRIMICORD_SERVER_URL
npx wrangler secret put TAILSCALE_AUTH_KEY
npm run deploy
```

O rate limiter aceita no máximo cinco tentativas por minuto por origem. As
respostas de ativação usam `Cache-Control: no-store`.
