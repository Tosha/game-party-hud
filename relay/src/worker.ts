export { PartyRoom } from "./room";

interface Env {
  PARTY_ROOM: DurableObjectNamespace;
}

const PARTY_PATH = /^\/party\/([A-Za-z0-9_-]{1,32})$/;

// Browser WebSockets always set the `Origin` header to the page that opened
// them, and the JS API doesn't expose a way to suppress it. Our desktop client
// (System.Net.WebSockets.ClientWebSocket) does NOT send Origin by default, so
// any incoming request with an Origin header that isn't on this list is
// almost certainly a browser-mediated probe — reject it.
//
// Empty list = desktop-only deployment. Add an explicit string here if you
// ever ship a browser/Electron variant that should be allowed in.
const ALLOWED_ORIGINS: readonly string[] = [];

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    const match = PARTY_PATH.exec(url.pathname);
    if (!match) return new Response("not found", { status: 404 });

    // Reject non-upgrade traffic at the edge so we don't spin up a Durable
    // Object just to hand back a 426 (and so the test runtime doesn't have to
    // tear one down on every probe).
    if (request.headers.get("Upgrade") !== "websocket") {
      return new Response("expected WebSocket upgrade", { status: 426 });
    }

    // Origin allowlist: see ALLOWED_ORIGINS comment above.
    const origin = request.headers.get("Origin");
    if (origin !== null && !ALLOWED_ORIGINS.includes(origin)) {
      return new Response("forbidden", { status: 403 });
    }

    const partyId = match[1]!;
    const id = env.PARTY_ROOM.idFromName(partyId);
    const stub = env.PARTY_ROOM.get(id);
    return stub.fetch(request);
  },
};
