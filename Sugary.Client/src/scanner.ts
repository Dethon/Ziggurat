// The parts of barcode scanning that are plain arithmetic, kept apart from the camera and the
// decoder so they can be tested.

export interface Pixels {
  data: Uint8ClampedArray;
  width: number;
  height: number;
}

export interface Size {
  width: number;
  height: number;
}

export interface Box extends Size {
  x: number;
  y: number;
}

const DIGITS = /^\d+$/;

// UPC-E is UPC-A with its zeros squeezed out; the check digit belongs to the long form.
function expandUpcE(code: string): string {
  const [system, a, b, c, d, e, last, check] = code;
  const body =
    last <= "2"
      ? `${a}${b}${last}0000${c}${d}${e}`
      : last === "3"
        ? `${a}${b}${c}00000${d}${e}`
        : last === "4"
          ? `${a}${b}${c}${d}00000${e}`
          : `${a}${b}${c}${d}${e}0000${last}`;
  return `${system}${body}${check}`;
}

// Every retail code ends in a digit that makes the weighted sum, counted from the right, a
// multiple of ten.
function checksHold(code: string): boolean {
  const sum = [...code].reverse().reduce((total, digit, index) => total + Number(digit) * (index % 2 === 0 ? 1 : 3), 0);
  return sum % 10 === 0;
}

export function isProductCode(code: string): boolean {
  if (!DIGITS.test(code)) {
    return false;
  }
  switch (code.length) {
    case 13:
    case 12:
      return checksHold(code);
    case 8:
      // Eight digits are EAN-8, or UPC-E when they start with 0 or 1; either reading may hold.
      return checksHold(code) || ((code[0] === "0" || code[0] === "1") && checksHold(expandUpcE(code)));
    default:
      return false;
  }
}

export interface TallyOptions {
  // Readings a code needs before it is believed.
  needed: number;
  // How far ahead of any other code it must be.
  lead: number;
  // Readings older than this no longer count.
  windowMs: number;
}

export interface Tally {
  add(codes: readonly string[], at: number): string | null;
  reset(): void;
}

// A worn or curved barcode is now and then misread into another number whose check digit
// happens to hold, about one misreading in ten. Such misreadings differ from one look to the
// next while the real code keeps coming back, so a code is believed only once several readings
// agree on it and nothing else is close.
export function createTally({ needed, lead, windowMs }: TallyOptions): Tally {
  let readings: { code: string; at: number }[] = [];

  return {
    add(codes, at) {
      readings = readings.filter((reading) => at - reading.at <= windowMs);
      for (const code of new Set(codes)) {
        if (isProductCode(code)) {
          readings.push({ code, at });
        }
      }

      const counts = new Map<string, number>();
      for (const { code } of readings) {
        counts.set(code, (counts.get(code) ?? 0) + 1);
      }
      const [first, second] = [...counts].sort((a, b) => b[1] - a[1]);
      return first && first[1] >= needed && first[1] - (second?.[1] ?? 0) >= lead ? first[0] : null;
    },
    reset() {
      readings = [];
    },
  };
}

// For one photo read several ways: the code most passes agree on, provided at least two do.
export function agreed(passes: readonly (readonly string[])[]): string | null {
  const tally = createTally({ needed: 2, lead: 1, windowMs: Infinity });
  return passes.reduce<string | null>((_, codes) => tally.add(codes, 0), null);
}

// The camera picture is shown cropped to fill its box (object-fit: cover). This finds the
// camera pixels that lie behind a box drawn on screen.
export function coverCrop(video: Size, element: Size, box: Box): Box {
  const scale = Math.max(element.width / video.width, element.height / video.height);
  const left = (element.width - video.width * scale) / 2;
  const top = (element.height - video.height * scale) / 2;
  const x = Math.max(0, Math.round((box.x - left) / scale));
  const y = Math.max(0, Math.round((box.y - top) / scale));
  return {
    x,
    y,
    width: Math.min(video.width - x, Math.round((box.x + box.width - left) / scale) - x),
    height: Math.min(video.height - y, Math.round((box.y + box.height - top) / scale) - y),
  };
}

// Bars printed faintly, or washed out by glare, come out thinner than the gaps between them
// and the decoder gives up. Taking the darkest of each pixel's neighbours widens every bar by
// a pixel on each side. The result is grey.
export function thicken(source: Pixels): Pixels {
  const { width, height } = source;
  const grey = new Uint8Array(width * height);
  for (let i = 0, p = 0; i < grey.length; i++, p += 4) {
    grey[i] = (source.data[p] * 77 + source.data[p + 1] * 150 + source.data[p + 2] * 29) >> 8;
  }

  // The minimum over a 3x3 square is the minimum along rows, then along columns.
  const rows = new Uint8Array(grey.length);
  for (let y = 0; y < height; y++) {
    const start = y * width;
    for (let x = 0; x < width; x++) {
      const i = start + x;
      rows[i] = Math.min(grey[i], x > 0 ? grey[i - 1] : 255, x < width - 1 ? grey[i + 1] : 255);
    }
  }

  const data = new Uint8ClampedArray(source.data.length);
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const i = y * width + x;
      const value = Math.min(rows[i], y > 0 ? rows[i - width] : 255, y < height - 1 ? rows[i + width] : 255);
      const p = i * 4;
      data[p] = data[p + 1] = data[p + 2] = value;
      data[p + 3] = 255;
    }
  }
  return { data, width, height };
}
