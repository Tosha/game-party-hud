export { PartyRoom } from "./room";

interface Env {
  PARTY_ROOM: DurableObjectNamespace;
}

const PARTY_PATH = /^\/party\/([A-Za-z0-9_-]{1,32})$/;

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

    const partyId = match[1]!;
    const id = env.PARTY_ROOM.idFromName(partyId);
    const stub = env.PARTY_ROOM.get(id);
    return stub.fetch(request);
  },
};
