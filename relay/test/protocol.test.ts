import { describe, expect, it } from "vitest";
import {
  type ClientMessage,
  type ServerMessage,
  decodeClientMessage,
  encodeServerMessage,
} from "../src/protocol";

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
});
