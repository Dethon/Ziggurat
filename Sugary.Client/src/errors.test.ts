import { afterEach, describe, expect, it, vi } from "vitest";
import { api } from "./api";
import { labelFailureMessage } from "./errors";
import { translator } from "./i18n";

// The server's answer as ASP.NET writes a problem, read through the app's own request.
function answering(status: number, problem: Record<string, unknown>) {
  vi.stubGlobal("fetch", async () =>
    new Response(JSON.stringify({ title: "Unprocessable Entity", status, ...problem }), {
      status,
      headers: { "Content-Type": "application/problem+json" },
    }),
  );
}

async function readingFails(): Promise<unknown> {
  try {
    await api.readLabel(new Blob(["jpeg"], { type: "image/jpeg" }), "es");
  } catch (failure) {
    return failure;
  }
  throw new Error("the label was read");
}

describe("a label that could not be read", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const es = translator("es");

  // The server words no reason of its own when the model gave none, so her Spanish screen does not
  // show an English sentence: the app says it in her language.
  it("says so in her language when no reason came", async () => {
    answering(422, { detail: "" });

    expect(labelFailureMessage(await readingFails(), es)).toBe("No he podido leer esa etiqueta.");
  });

  it("shows the model's reason as it came", async () => {
    answering(422, { detail: "Esto es un plato de paella, no una etiqueta." });

    expect(labelFailureMessage(await readingFails(), es)).toBe("Esto es un plato de paella, no una etiqueta.");
  });

  it("shows any other failure as every other screen does", async () => {
    answering(502, { title: "Bad Gateway", detail: "The label reader is not connected right now." });

    expect(labelFailureMessage(await readingFails(), es)).toBe("The label reader is not connected right now.");
  });
});
