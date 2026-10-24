import { api } from "./api";
import type { Language } from "./i18n";
import type { SavePushSubscription } from "./types";

// Where the pen reminders stand on this phone. Unavailable: the server has no keys. Unsupported:
// this browser has no push, as an iPhone's has none outside the installed app.
export type PushState = "unavailable" | "unsupported" | "denied" | "off" | "on";

export function keyBytes(base64url: string): Uint8Array<ArrayBuffer> {
  const base64 = base64url.replace(/-/g, "+").replace(/_/g, "/");
  const binary = atob(base64.padEnd(Math.ceil(base64.length / 4) * 4, "="));
  return Uint8Array.from(binary, (char) => char.charCodeAt(0));
}

export function toSave(subscription: PushSubscriptionJSON, language: Language): SavePushSubscription | null {
  const { endpoint, keys } = subscription;
  return endpoint && keys?.p256dh && keys.auth
    ? { endpoint, p256dh: keys.p256dh, auth: keys.auth, language }
    : null;
}

const supported = () => "serviceWorker" in navigator && "PushManager" in window && "Notification" in window;

async function current(): Promise<PushSubscription | null> {
  const registration = await navigator.serviceWorker.ready;
  return registration.pushManager.getSubscription();
}

async function save(subscription: PushSubscription, language: Language): Promise<void> {
  const body = toSave(subscription.toJSON(), language);
  if (body) {
    await api.savePushSubscription(body);
  }
}

export async function pushState(publicKey: string | null): Promise<PushState> {
  if (!publicKey) {
    return "unavailable";
  }
  if (!supported()) {
    return "unsupported";
  }
  if (Notification.permission === "denied") {
    return "denied";
  }
  return Notification.permission === "granted" && (await current()) ? "on" : "off";
}

// Asked from her tap: a browser shows the permission prompt only for one.
export async function turnOn(publicKey: string, language: Language): Promise<PushState> {
  if ((await Notification.requestPermission()) !== "granted") {
    return Notification.permission === "denied" ? "denied" : "off";
  }
  const registration = await navigator.serviceWorker.ready;
  const subscription =
    (await registration.pushManager.getSubscription()) ??
    (await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: keyBytes(publicKey) }));
  await save(subscription, language);
  return "on";
}

export async function turnOff(): Promise<void> {
  const subscription = await current();
  if (subscription) {
    await api.deletePushSubscription(subscription.endpoint);
    await subscription.unsubscribe();
  }
}

// Handed to the server again when the app opens and when its language changes: a push service can
// renew a subscription on its own, and the reminders are written in the language last given.
export async function resync(language: Language): Promise<void> {
  if (!supported() || Notification.permission !== "granted") {
    return;
  }
  const subscription = await current();
  if (subscription) {
    await save(subscription, language);
  }
}
