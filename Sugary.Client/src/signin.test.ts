import { describe, expect, it } from "vitest";
import { signInUrl } from "./signin";

describe("a person whose session is gone", () => {
  it("is sent to sign in on this host, and back to the page they were on", () => {
    expect(signInUrl("https://sugary.example.com/log?day=2026-10-11#meal")).toBe(
      "/accounts/sign-in?returnUrl=https%3A%2F%2Fsugary.example.com%2Flog%3Fday%3D2026-10-11%23meal",
    );
  });
});
