import { describe, expect, it } from "vitest";
import { keyBytes, toSave } from "./push";

describe("keyBytes", () => {
  // The server's key is base64url without padding, as VAPID keys are written.
  it("reads a base64url key into the bytes the browser subscribes with", () => {
    expect([...keyBytes("AQID_-8")]).toEqual([1, 2, 3, 255, 239]);
  });
});

describe("toSave", () => {
  it("hands the server the endpoint, the keys and the app's language", () => {
    expect(
      toSave({ endpoint: "https://push.example.com/a", keys: { p256dh: "key", auth: "secret" } }, "en"),
    ).toEqual({ endpoint: "https://push.example.com/a", p256dh: "key", auth: "secret", language: "en" });
  });

  it("is nothing for a subscription missing what a push needs", () => {
    expect(toSave({ endpoint: "https://push.example.com/a", keys: {} }, "es")).toBeNull();
    expect(toSave({ keys: { p256dh: "key", auth: "secret" } }, "es")).toBeNull();
  });
});
