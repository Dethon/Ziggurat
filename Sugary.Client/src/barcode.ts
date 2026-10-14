import { agreed, createTally, thicken, type Box, type Pixels } from "./scanner";

// What a decoder is asked to read: a still picture's pixels, or the live camera picture.
type Readable = ImageData | HTMLVideoElement;
type Decoder = (image: Readable) => Promise<string[]>;

const FORMATS = ["ean_13", "ean_8", "upc_a", "upc_e"] as const;

// Photos are read at up to this size. Larger than what is uploaded for a label, because thin
// bars need the pixels; smaller than a 12 MP original, which costs memory for nothing.
const STILL_EDGE = 2000;
// One photo is read at several sizes, plain and with its bars thickened, and a code counts
// only when at least two of the readings agree. Measured on 25 photos of real barcodes: one
// plain reading got 19 right and, on a distorted one, returned a wrong number; these six got
// 23 right and none wrong.
const STILL_PASSES: readonly (readonly [scale: number, thick: boolean])[] = [
  [1, false],
  [1, true],
  [1.5, false],
  [0.75, true],
  [0.75, false],
  [1.5, true],
];
// A photo in which the first readings find nothing at all is a photo of something else, most
// often the nutrition label, and is not worth the rest.
const STILL_PASSES_BEFORE_GIVING_UP = 3;

// Live, each look at the camera takes one view of the picture in turn: the part on screen as
// it is, with thicker bars and enlarged, and now and then the whole camera picture in case
// the barcode is just off screen.
type View = { whole: true } | { whole?: false; scale: number; thick: boolean };
const LIVE_VIEWS: readonly View[] = [
  { scale: 1, thick: false },
  { scale: 1, thick: true },
  { scale: 1.5, thick: false },
  { whole: true },
  { scale: 1.5, thick: true },
];
const WHOLE_EDGE = 1280;
// How long the phone's own detector is given before the slower readings join in.
const NATIVE_HEAD_START_MS = 1500;

let software: Promise<Decoder> | null = null;

// The decoder is a WebAssembly module, loaded only when a barcode is first looked for. It is
// served from this app rather than the library's default CDN so scanning works wherever the
// app itself does.
function softwareDecoder(): Promise<Decoder> {
  software ??= (async () => {
    const [{ BarcodeDetector, prepareZXingModule }, { default: wasmUrl }] = await Promise.all([
      import("barcode-detector/ponyfill"),
      import("zxing-wasm/reader/zxing_reader.wasm?url"),
    ]);
    prepareZXingModule({ overrides: { locateFile: () => wasmUrl } });
    const detector = new BarcodeDetector({ formats: [...FORMATS] });
    return async (image: Readable) => (await detector.detect(image)).map((found) => found.rawValue);
  })();
  software.catch(() => (software = null));
  return software;
}

interface NativeDetector {
  detect(image: Readable): Promise<{ rawValue: string }[]>;
}

interface NativeDetectorClass {
  new (options: { formats: string[] }): NativeDetector;
  getSupportedFormats(): Promise<string[]>;
}

// Android's Chrome reads barcodes itself, faster and off the page's thread. Other browsers
// either lack the detector or have one that knows no retail formats.
async function nativeDecoder(): Promise<Decoder | null> {
  const Native = (globalThis as { BarcodeDetector?: NativeDetectorClass }).BarcodeDetector;
  if (!Native) {
    return null;
  }
  try {
    const supported = await Native.getSupportedFormats();
    if (!supported.includes("ean_13")) {
      return null;
    }
    const detector = new Native({ formats: FORMATS.filter((format) => supported.includes(format)) });
    return async (image: Readable) => (await detector.detect(image)).map((found) => found.rawValue);
  } catch {
    return null;
  }
}

// One drawing surface serves every reading of a photo or a camera session; a fresh one per
// look would be several megabytes of garbage ten times a second.
function surface() {
  const canvas = document.createElement("canvas");
  const context = canvas.getContext("2d", { willReadFrequently: true })!;
  return (width: number, height: number) => {
    canvas.width = Math.max(1, Math.round(width));
    canvas.height = Math.max(1, Math.round(height));
    return { canvas, context };
  };
}

function toImageData(pixels: Pixels): ImageData {
  return new ImageData(pixels.data as Uint8ClampedArray<ArrayBuffer>, pixels.width, pixels.height);
}

async function decoded(photo: Blob): Promise<ImageBitmap> {
  try {
    return await createImageBitmap(photo, { imageOrientation: "from-image" });
  } catch {
    return await createImageBitmap(photo);
  }
}

// Reads the barcode in a photo, or returns null when there is none it can be sure of.
export async function readBarcode(photo: Blob): Promise<string | null> {
  try {
    const [decode, bitmap] = await Promise.all([softwareDecoder(), decoded(photo)]);
    try {
      const fit = Math.min(1, STILL_EDGE / Math.max(bitmap.width, bitmap.height));
      const sized = surface();
      const passes: string[][] = [];
      for (const [scale, thick] of STILL_PASSES) {
        const { context, canvas } = sized(bitmap.width * fit * scale, bitmap.height * fit * scale);
        context.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
        const pixels = context.getImageData(0, 0, canvas.width, canvas.height);
        passes.push(await decode(thick ? toImageData(thicken(pixels)) : pixels));
        if (passes.length === STILL_PASSES_BEFORE_GIVING_UP && passes.every((codes) => codes.length === 0)) {
          return null;
        }
      }
      return agreed(passes);
    } finally {
      bitmap.close();
    }
  } catch {
    return null;
  }
}

export interface FrameReader {
  // Takes one look at the camera picture. `shown` is the part of it, in camera pixels, that
  // the person can see on screen. Returns a code once enough looks agree on it.
  look(video: HTMLVideoElement, shown: Box, at: number): Promise<string | null>;
}

export async function openFrameReader(): Promise<FrameReader> {
  const native = await nativeDecoder();
  // Without a native detector the software one is needed at once; with one it can load behind.
  const softwareReady = softwareDecoder();
  if (!native) {
    await softwareReady;
  } else {
    softwareReady.catch(() => undefined);
  }

  const tally = createTally({ needed: 3, lead: 2, windowMs: 3000 });
  const sized = surface();
  let nativeWorks = native !== null;
  let started: number | null = null;
  let turn = 0;

  async function view(video: HTMLVideoElement, shown: Box, decode: Decoder): Promise<string[]> {
    const next = LIVE_VIEWS[turn++ % LIVE_VIEWS.length];
    if (next.whole) {
      const fit = Math.min(1, WHOLE_EDGE / Math.max(video.videoWidth, video.videoHeight));
      const { context, canvas } = sized(video.videoWidth * fit, video.videoHeight * fit);
      context.drawImage(video, 0, 0, canvas.width, canvas.height);
      return decode(context.getImageData(0, 0, canvas.width, canvas.height));
    }
    const { context, canvas } = sized(shown.width * next.scale, shown.height * next.scale);
    context.drawImage(video, shown.x, shown.y, shown.width, shown.height, 0, 0, canvas.width, canvas.height);
    const pixels = context.getImageData(0, 0, canvas.width, canvas.height);
    return decode(next.thick ? toImageData(thicken(pixels)) : pixels);
  }

  return {
    async look(video, shown, at) {
      started ??= at;
      const codes: string[] = [];
      if (native && nativeWorks) {
        try {
          codes.push(...(await native(video)));
        } catch {
          // A detector that fails once is not asked again; the software one carries on.
          nativeWorks = false;
        }
      }
      if (!nativeWorks || at - started >= NATIVE_HEAD_START_MS) {
        try {
          codes.push(...(await view(video, shown, await softwareReady)));
        } catch (failure) {
          if (!nativeWorks) {
            throw failure;
          }
        }
      }
      return tally.add(codes, at);
    },
  };
}
