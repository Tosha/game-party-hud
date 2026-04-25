import { describe, expect, it } from "vitest";
import {
  type ClientMessage,
  type ServerMessage,
  decodeClientMessage,
  encodeServerMessage,
} from "../src/protocol";
import { fixtures } from "./fixtures";

describe("protocol", () => {
  it("decodes a join frame", () => {
    const raw = '{"type":"join","peerId":"a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe"}';
    const msg = decodeClientMessage(raw);
    expect(msg).toEqual({
      type: "join",
      peerId: "a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe",
    });
  });

  it("decodes a broadcast frame", () => {
    const raw = '{"type":"broadcast","payload":"{\\"type\\":\\"state\\",\\"hp\\":0.5}"}';
    const msg = decodeClientMessage(raw);
    expect(msg).toEqual({ type: "broadcast", payload: '{"type":"state","hp":0.5}' });
  });

  it("rejects unknown client types", () => {
    expect(decodeClientMessage('{"type":"something"}')).toBeNull();
  });

  it("rejects malformed JSON", () => {
    expect(decodeClientMessage("not json")).toBeNull();
  });

  it("rejects a join without peerId", () => {
    expect(decodeClientMessage('{"type":"join"}')).toBeNull();
  });

  it("encodes welcome", () => {
    const raw = encodeServerMessage({
      type: "welcome",
      peerId: "abc",
      members: ["def", "ghi"],
    });
    expect(JSON.parse(raw)).toEqual({
      type: "welcome",
      peerId: "abc",
      members: ["def", "ghi"],
    });
  });

  it("encodes peer-joined", () => {
    const raw = encodeServerMessage({ type: "peer-joined", peerId: "xyz" });
    expect(JSON.parse(raw)).toEqual({ type: "peer-joined", peerId: "xyz" });
  });

  it("encodes a message relay frame", () => {
    const msg: ServerMessage = {
      type: "message",
      fromPeerId: "a",
      payload: '{"hp":0.3}',
    };
    const raw = encodeServerMessage(msg);
    expect(JSON.parse(raw)).toEqual(msg);
  });

  it("client-to-server fixtures round-trip through the decoder", () => {
    const join = decodeClientMessage(fixtures.join);
    expect(join).toEqual({ type: "join", peerId: "a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe" });

    const bc = decodeClientMessage(fixtures.broadcast);
    expect(bc).toEqual({ type: "broadcast", payload: '{"type":"state","hp":0.5}' });
  });

  it("server-to-client fixtures match the encoder output byte-for-byte", () => {
    expect(encodeServerMessage({
      type: "welcome",
      peerId: "a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe",
      members: ["peer-b", "peer-c"],
    })).toBe(fixtures.welcome);

    expect(encodeServerMessage({ type: "peer-joined", peerId: "peer-b" })).toBe(fixtures.peerJoined);
    expect(encodeServerMessage({ type: "peer-left",   peerId: "peer-b" })).toBe(fixtures.peerLeft);
    expect(encodeServerMessage({
      type: "message",
      fromPeerId: "peer-b",
      payload: '{"type":"state","hp":0.5}',
    })).toBe(fixtures.message);
    expect(encodeServerMessage({ type: "error", reason: "party-full" })).toBe(fixtures.errorFull);
  });
});
