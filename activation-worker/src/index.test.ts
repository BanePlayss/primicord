import { describe, expect, it, vi } from "vitest";
import { handleRequest, normalizeCode, sha256, timingSafeEqual, type Env } from "./index";

function environment(hash: string): Env {
  return {
    ACTIVATION_RATE_LIMITER: { limit: vi.fn(async () => ({ success: true })) },
    GROUP_CODE_HASH: hash,
    PRIMICORD_SERVER_URL: "http://100.64.2.73:8765",
    TAILSCALE_AUTH_KEY: "tskey-auth-test-only-12345678901234567890",
  };
}

function activate(code: string, installId = "2c9c70a3-3a08-49b9-a1cb-28f8ac1f6cf8"): Request {
  return new Request("https://activation.example/v1/activate", {
    method: "POST",
    headers: { "content-type": "application/json", "cf-connecting-ip": "203.0.113.10" },
    body: JSON.stringify({ code, installId, version: "0.6.15" }),
  });
}

describe("activation worker", () => {
  it("normalizes a code without changing its words", () => {
    expect(normalizeCode("  sala exemplo  ")).toBe("SALA-EXEMPLO");
  });

  it("compares hashes without an early content exit", () => {
    expect(timingSafeEqual("a".repeat(64), "a".repeat(64))).toBe(true);
    expect(timingSafeEqual("a".repeat(64), "b" + "a".repeat(63))).toBe(false);
  });

  it("returns the group configuration only for the right code", async () => {
    const hash = await sha256("EXAMPLE-GROUP-CODE");
    const response = await handleRequest(activate("example group code"), environment(hash));
    expect(response.status).toBe(200);
    expect(await response.json()).toMatchObject({
      serverUrl: "http://100.64.2.73:8765",
      authKey: "tskey-auth-test-only-12345678901234567890",
    });
    expect(response.headers.get("cache-control")).toContain("no-store");
  });

  it("does not reveal whether a mistyped code was close", async () => {
    const hash = await sha256("EXAMPLE-GROUP-CODE");
    const response = await handleRequest(activate("EXAMPLE-GROUP-CODF"), environment(hash));
    expect(response.status).toBe(401);
    expect(await response.json()).toEqual({ error: "invalid_code" });
  });

  it("rate limits before checking the code", async () => {
    const hash = await sha256("EXAMPLE-GROUP-CODE");
    const env = environment(hash);
    env.ACTIVATION_RATE_LIMITER = { limit: vi.fn(async () => ({ success: false })) };
    const response = await handleRequest(activate("EXAMPLE-GROUP-CODE"), env);
    expect(response.status).toBe(429);
  });
});
