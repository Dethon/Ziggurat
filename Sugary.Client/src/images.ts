// Photos are shrunk on the phone before upload: a 12 MP original costs mobile data and model
// tokens without making a plate of food any easier to read.
const UPLOAD_EDGE = 1568;
const THUMBNAIL_EDGE = 480;

async function draw(source: Blob, maxEdge: number): Promise<HTMLCanvasElement> {
  const bitmap = await createImageBitmap(source, { imageOrientation: "from-image" });
  const scale = Math.min(1, maxEdge / Math.max(bitmap.width, bitmap.height));
  const canvas = document.createElement("canvas");
  canvas.width = Math.round(bitmap.width * scale);
  canvas.height = Math.round(bitmap.height * scale);
  canvas.getContext("2d")!.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
  bitmap.close();
  return canvas;
}

export async function forUpload(source: Blob): Promise<Blob> {
  const canvas = await draw(source, UPLOAD_EDGE);
  return new Promise((resolve, reject) =>
    canvas.toBlob((blob) => (blob ? resolve(blob) : reject(new Error("encode failed"))), "image/jpeg", 0.85),
  );
}

export async function thumbnail(source: Blob): Promise<string> {
  return (await draw(source, THUMBNAIL_EDGE)).toDataURL("image/jpeg", 0.7);
}
