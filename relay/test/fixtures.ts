// CANONICAL wire strings. Exact byte-for-byte match is the contract between
// TS server and C# client — src/GamePartyHud/Network/RelayProtocol.cs and
// tests/GamePartyHud.Tests/Network/RelayProtocolTests.cs MUST reproduce
// these exact strings. If you change one, update the other.

export const fixtures = {
  join:        '{"type":"join","peerId":"a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe"}',
  broadcast:   '{"type":"broadcast","payload":"{\\"type\\":\\"state\\",\\"hp\\":0.5}"}',
  welcome:     '{"type":"welcome","peerId":"a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe","members":["peer-b","peer-c"]}',
  peerJoined:  '{"type":"peer-joined","peerId":"peer-b"}',
  peerLeft:    '{"type":"peer-left","peerId":"peer-b"}',
  message:     '{"type":"message","fromPeerId":"peer-b","payload":"{\\"type\\":\\"state\\",\\"hp\\":0.5}"}',
  errorFull:   '{"type":"error","reason":"party-full"}',
} as const;
