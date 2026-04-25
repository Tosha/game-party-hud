export default {
  async fetch(_request: Request, _env: unknown): Promise<Response> {
    return new Response("not implemented", { status: 501 });
  },
};

// Exported so wrangler's migrations resolve the class name. Actual implementation
// lands in Task 4 onwards.
export class PartyRoom {
  constructor(_state: DurableObjectState, _env: unknown) {}
  async fetch(_request: Request): Promise<Response> {
    return new Response("not implemented", { status: 501 });
  }
}
