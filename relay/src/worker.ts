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

    const partyId = match[1]!;
    const id = env.PARTY_ROOM.idFromName(partyId);
    const stub = env.PARTY_ROOM.get(id);
    return stub.fetch(request);
  },
};
