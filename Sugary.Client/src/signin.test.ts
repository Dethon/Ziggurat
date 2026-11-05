import { describe, expect, it } from "vitest";
import { isSuspension } from "./api";
import { noticeUrl, signInUrl } from "./signin";

describe("a person whose session is gone", () => {
  it("is sent to sign in on this host, and back to the page they were on", () => {
    expect(signInUrl("https://sugary.example.com/log?day=2026-10-11#meal")).toBe(
      "/accounts/sign-in?returnUrl=https%3A%2F%2Fsugary.example.com%2Flog%3Fday%3D2026-10-11%23meal",
    );
  });
});

describe("a person whose household was suspended while the app was open", () => {
  it("is sent to the household's notice on this host, which brings them back once it is resumed", () => {
    expect(noticeUrl("https://sugary.example.com/log")).toBe(
      "/accounts/account?returnUrl=https%3A%2F%2Fsugary.example.com%2Flog",
    );
  });

  it("is told apart from the app's own refusals by the proxy's word for it", () => {
    const suspended = new Response("Your household is suspended", {
      status: 403,
      headers: { "X-Ziggurat-Refusal": "suspended" },
    });
    const sugaryOff = new Response(JSON.stringify({ code: "sugary-off" }), {
      status: 403,
      headers: { "Content-Type": "application/json" },
    });

    expect(isSuspension(suspended)).toBe(true);
    expect(isSuspension(sugaryOff)).toBe(false);
  });
});
