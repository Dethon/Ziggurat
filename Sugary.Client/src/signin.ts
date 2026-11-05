// Where a person whose session is gone is sent: the accounts service's sign-in, which the proxy
// routes on this host as on every app's, and back to the page they were on once they are in. The app
// has no sign-in of its own; the proxy names the person on every request.
export function signInUrl(here: string): string {
  return `/accounts/sign-in?returnUrl=${encodeURIComponent(here)}`;
}

// A full load, so the proxy and the accounts service see the request, never the installed app
// answering it from its own cache.
export function goSignIn(): void {
  window.location.assign(signInUrl(window.location.href));
}

// Where a person whose household the operator suspended is sent: its notice, which the accounts
// service serves on this host too, asks again until the household is resumed and comes back here.
export function noticeUrl(here: string): string {
  return `/accounts/account?returnUrl=${encodeURIComponent(here)}`;
}

export function goNotice(): void {
  window.location.assign(noticeUrl(window.location.href));
}
