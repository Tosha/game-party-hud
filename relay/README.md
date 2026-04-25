# gph-relay — WebSocket relay for Game Party HUD

Stateless Cloudflare Worker + Durable Object that routes party-state broadcasts
between GamePartyHud clients. One Durable Object per party id; each broadcast
fans out to the other members. No storage.

## Setup (one-time)

1. Create a free [Cloudflare account](https://dash.cloudflare.com/sign-up).
2. `npm i` inside this folder.
3. `npx wrangler login`.
4. `npx wrangler deploy`.
5. Paste the deployed URL (e.g. `https://gph-relay.<you>.workers.dev`) into
   `AppConfig.Defaults.RelayUrl` — replacing `https://` with `wss://`.

## Costs

Well inside the Cloudflare free tier for the forecast usage (≤ 100 k requests /
day, ≤ 1 GiB egress, Durable Objects free on Workers Paid only — so if you're on
free plan, use the `workerd` DO free beta or upgrade to Workers Paid at $5/mo
flat).

## Local dev

```bash
npm run dev          # wrangler dev — serves on http://localhost:8787
npm test             # vitest with Miniflare
```

## Wire protocol

See `src/protocol.ts` and the fixtures in `test/fixtures.ts`. Those strings are
also the source of truth for the C# client's JSON parsing; keep them in sync.
