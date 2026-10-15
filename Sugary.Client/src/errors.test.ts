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

  it("says a reading that never came back in her language, not the server's English", async () => {
    answering(502, { title: "Bad Gateway", detail: "The label reader is not connected right now." });

    expect(labelFailureMessage(await readingFails(), es)).toBe(es("labelNoAnswer"));
  });
});

describe("an estimate that took too long", () => {
  it("is told in her language, not the server's English", async () => {
    const { ApiError } = await import("./api");
    const { errorMessage } = await import("./errors");
    const failure = new ApiError(504, "The estimate took too long. Try again, or count this meal by hand.");
    expect(errorMessage(failure, translator("es"))).toBe(translator("es")("errorTimeout"));
  });
});

describe("an answer the server could not read", () => {
  it("is told in her language for an estimate and for a label", async () => {
    const { ApiError } = await import("./api");
    const { errorMessage, labelFailureMessage } = await import("./errors");
    const es = translator("es");
    expect(errorMessage(new ApiError(502, "The model returned no answer."), es)).toBe(es("errorUnreadable"));
    expect(labelFailureMessage(new ApiError(502, ""), es)).toBe(es("labelNoAnswer"));
  });
});
