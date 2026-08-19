interface RateLimitBinding {
  limit(options: { key: string }): Promise<{ success: boolean }>;
}

export interface Env {
  ACTIVATION_RATE_LIMITER: RateLimitBinding;
  GROUP_CODE_HASH: string;
  PRIMICORD_SERVER_URL: string;
  TAILSCALE_AUTH_KEY?: string;
  TAILSCALE_OAUTH_CLIENT_ID?: string;
  TAILSCALE_OAUTH_CLIENT_SECRET?: string;
  TAILSCALE_TAG?: string;
}

interface ActivationRequest {
  code?: unknown;
  installId?: unknown;
  version?: unknown;
}

const jsonHeaders = {
  "content-type": "application/json; charset=utf-8",
  "cache-control": "no-store, max-age=0",
  "x-content-type-options": "nosniff",
} as const;

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: jsonHeaders });
}

export function normalizeCode(value: string): string {
  return value.trim().toUpperCase().replace(/[\s_]+/g, "-");
}

function hex(bytes: ArrayBuffer): string {
  return [...new Uint8Array(bytes)]
    .map((value) => value.toString(16).padStart(2, "0"))
    .join("");
}

export async function sha256(value: string): Promise<string> {
  return hex(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value)));
}

export function timingSafeEqual(left: string, right: string): boolean {
  if (left.length !== right.length) return false;
  let different = 0;
  for (let index = 0; index < left.length; index++)
    different |= left.charCodeAt(index) ^ right.charCodeAt(index);
  return different === 0;
}

function validServerUrl(value: string): boolean {
  try {
    const url = new URL(value);
    return url.protocol === "http:" && url.hostname.length > 0 && url.port === "8765";
  } catch {
    return false;
  }
}

async function mintTailscaleKey(env: Env): Promise<string> {
  if (env.TAILSCALE_AUTH_KEY?.startsWith("tskey-auth-") === true)
    return env.TAILSCALE_AUTH_KEY;

  const clientId = env.TAILSCALE_OAUTH_CLIENT_ID?.trim();
  const clientSecret = env.TAILSCALE_OAUTH_CLIENT_SECRET?.trim();
  const tag = env.TAILSCALE_TAG?.trim();
  if (!clientId || !clientSecret || !tag?.startsWith("tag:"))
    throw new Error("activation service has no Tailscale credential");

  const tokenBody = new URLSearchParams({
    grant_type: "client_credentials",
    client_id: clientId,
    client_secret: clientSecret,
    scope: "auth_keys",
    tags: tag,
  });
  const tokenResponse = await fetch("https://api.tailscale.com/api/v2/oauth/token", {
    method: "POST",
    headers: { "content-type": "application/x-www-form-urlencoded" },
    body: tokenBody,
  });
  if (!tokenResponse.ok) throw new Error("Tailscale OAuth rejected the credential");
  const token = (await tokenResponse.json()) as { access_token?: unknown };
  if (typeof token.access_token !== "string" || token.access_token.length < 20)
    throw new Error("Tailscale OAuth returned no access token");

  const keyResponse = await fetch("https://api.tailscale.com/api/v2/tailnet/-/keys", {
    method: "POST",
    headers: {
      authorization: `Bearer ${token.access_token}`,
      "content-type": "application/json",
    },
    body: JSON.stringify({
      capabilities: {
        devices: {
          create: {
            reusable: false,
            ephemeral: false,
            preauthorized: true,
            tags: [tag],
          },
        },
      },
      expirySeconds: 600,
      description: "Primicord installer activation",
    }),
  });
  if (!keyResponse.ok) throw new Error("Tailscale did not create the one-time auth key");
  const key = (await keyResponse.json()) as { key?: unknown };
  if (typeof key.key !== "string" || !key.key.startsWith("tskey-auth-"))
    throw new Error("Tailscale returned an invalid auth key");
  return key.key;
}

export async function handleRequest(request: Request, env: Env): Promise<Response> {
  const url = new URL(request.url);
  if (request.method === "GET" && url.pathname === "/health")
    return json({ ok: true, service: "primicord-activation", version: "0.6.15" });
  if (request.method !== "POST" || url.pathname !== "/v1/activate")
    return json({ error: "not_found" }, 404);

  const length = Number(request.headers.get("content-length") ?? "0");
  if (length > 2048) return json({ error: "request_too_large" }, 413);

  const actor = request.headers.get("cf-connecting-ip")
    ?? request.headers.get("x-forwarded-for")?.split(",")[0]?.trim()
    ?? "unknown";
  const allowed = await env.ACTIVATION_RATE_LIMITER.limit({ key: `activate:${actor}` });
  if (!allowed.success) return json({ error: "too_many_attempts" }, 429);

  let input: ActivationRequest;
  try {
    input = await request.json() as ActivationRequest;
  } catch {
    return json({ error: "invalid_request" }, 400);
  }
  if (typeof input.code !== "string" || input.code.length === 0 || input.code.length > 96)
    return json({ error: "invalid_code" }, 401);
  if (typeof input.installId !== "string" || !/^[a-f0-9-]{16,64}$/i.test(input.installId))
    return json({ error: "invalid_request" }, 400);

  const expected = env.GROUP_CODE_HASH?.trim().toLowerCase();
  if (!/^[a-f0-9]{64}$/.test(expected))
    return json({ error: "service_not_configured" }, 503);
  const actual = await sha256(normalizeCode(input.code));
  if (!timingSafeEqual(actual, expected))
    return json({ error: "invalid_code" }, 401);
  if (!validServerUrl(env.PRIMICORD_SERVER_URL))
    return json({ error: "service_not_configured" }, 503);

  try {
    const authKey = await mintTailscaleKey(env);
    return json({
      serverUrl: env.PRIMICORD_SERVER_URL.replace(/\/$/, ""),
      authKey,
      activationId: crypto.randomUUID(),
    });
  } catch (error) {
    console.error("activation failed", error instanceof Error ? error.message : "unknown error");
    return json({ error: "activation_temporarily_unavailable" }, 503);
  }
}

export default {
  fetch: handleRequest,
} satisfies ExportedHandler<Env>;
