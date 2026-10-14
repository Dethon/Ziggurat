import { describe, expect, it } from "vitest";
import { agreed, coverCrop, createTally, isProductCode, thicken } from "./scanner";

describe("product codes", () => {
  it("accepts EAN-13, EAN-8, UPC-A and UPC-E with a correct check digit", () => {
    expect(isProductCode("7290003060047")).toBe(true);
    expect(isProductCode("96385074")).toBe(true);
    expect(isProductCode("036000291452")).toBe(true);
    expect(isProductCode("01234565")).toBe(true);
  });

  it("rejects a wrong check digit, a wrong length and anything that is not digits", () => {
    expect(isProductCode("7290003060048")).toBe(false);
    expect(isProductCode("729000306004")).toBe(false);
    expect(isProductCode("12345")).toBe(false);
    expect(isProductCode("72900030600a7")).toBe(false);
    expect(isProductCode("")).toBe(false);
  });
});

describe("tally", () => {
  const tally = () => createTally({ needed: 3, lead: 2, windowMs: 3000 });

  it("accepts a code once enough readings agree", () => {
    const votes = tally();

    expect(votes.add(["7290003060047"], 0)).toBeNull();
    expect(votes.add(["7290003060047"], 100)).toBeNull();
    expect(votes.add(["7290003060047"], 200)).toBe("7290003060047");
  });

  // A hard barcode is sometimes misread into another number whose check digit happens to hold.
  it("is not fooled by a misreading that turns up beside the real code", () => {
    const votes = tally();

    expect(votes.add(["7290005437632", "0220405437632"], 0)).toBeNull();
    expect(votes.add(["0220405437632"], 100)).toBeNull();
    expect(votes.add(["7290005437632"], 200)).toBeNull();
    expect(votes.add(["7290005437632"], 300)).toBeNull();
    expect(votes.add(["7290005437632"], 400)).toBe("7290005437632");
  });

  it("holds back while two codes are level", () => {
    const votes = tally();

    for (let at = 0; at < 1000; at += 100) {
      expect(votes.add(["7290005437632", "0220405437632"], at)).toBeNull();
    }
  });

  it("forgets readings older than its window", () => {
    const votes = tally();

    votes.add(["7290003060047"], 0);
    votes.add(["7290003060047"], 100);
    expect(votes.add(["7290003060047"], 3200)).toBeNull();
  });

  it("counts a code once per reading and ignores codes that fail their check digit", () => {
    const votes = tally();

    expect(votes.add(["7290003060047", "7290003060047", "7290003060047"], 0)).toBeNull();
    expect(votes.add(["7290003060048"], 100)).toBeNull();
    expect(votes.add(["7290003060048"], 200)).toBeNull();
    expect(votes.add(["7290003060048"], 300)).toBeNull();
  });

  it("starts afresh after a reset", () => {
    const votes = tally();

    votes.add(["7290003060047"], 0);
    votes.add(["7290003060047"], 100);
    votes.reset();
    expect(votes.add(["7290003060047"], 200)).toBeNull();
  });
});

describe("agreement between passes over one photo", () => {
  it("takes the code most passes read, when at least two did", () => {
    expect(agreed([["7290003060047", "5296403060047"], ["7290003060047"], ["7290003060047"]])).toBe("7290003060047");
    expect(agreed([["7290003060047"], [], ["7290003060047"]])).toBe("7290003060047");
  });

  it("gives nothing for a single reading or a tie", () => {
    expect(agreed([["7290003060047"], [], []])).toBeNull();
    expect(agreed([["7290003060047"], ["5296403060047"]])).toBeNull();
    expect(agreed([[], []])).toBeNull();
  });
});

describe("cover crop", () => {
  it("maps a box on screen to the camera pixels behind it when the video is cropped at the sides", () => {
    // A 1920x1080 stream shown in a 360x360 box is scaled by 1/3 and loses 140 px either side.
    expect(coverCrop({ width: 1920, height: 1080 }, { width: 360, height: 360 }, { x: 36, y: 120, width: 288, height: 120 }))
      .toEqual({ x: 528, y: 360, width: 864, height: 360 });
  });

  it("maps a box on screen when the video is cropped top and bottom", () => {
    expect(coverCrop({ width: 1080, height: 1920 }, { width: 360, height: 360 }, { x: 0, y: 90, width: 360, height: 180 }))
      .toEqual({ x: 0, y: 690, width: 1080, height: 540 });
  });

  it("stays inside the frame", () => {
    expect(coverCrop({ width: 640, height: 480 }, { width: 320, height: 240 }, { x: -50, y: -50, width: 600, height: 600 }))
      .toEqual({ x: 0, y: 0, width: 640, height: 480 });
  });
});

describe("thicken", () => {
  const image = (rows: number[][]) => ({
    width: rows[0].length,
    height: rows.length,
    data: new Uint8ClampedArray(rows.flat().flatMap((v) => [v, v, v, 255])),
  });
  const grey = (pixels: { data: Uint8ClampedArray; width: number }) =>
    Array.from({ length: pixels.data.length / 4 / pixels.width }, (_, y) =>
      Array.from({ length: pixels.width }, (_, x) => pixels.data[(y * pixels.width + x) * 4]),
    );

  it("widens a thin dark bar by one pixel on each side", () => {
    const thin = image([
      [255, 255, 0, 255, 255],
      [255, 255, 0, 255, 255],
      [255, 255, 0, 255, 255],
    ]);

    expect(grey(thicken(thin))).toEqual([
      [255, 0, 0, 0, 255],
      [255, 0, 0, 0, 255],
      [255, 0, 0, 0, 255],
    ]);
  });

  it("leaves the picture it was given untouched and keeps it opaque", () => {
    const thin = image([[255, 0, 255]]);

    const thick = thicken(thin);

    expect(grey(thin)).toEqual([[255, 0, 255]]);
    expect([...thick.data.filter((_, i) => i % 4 === 3)]).toEqual([255, 255, 255]);
  });
});
