export function connectionPresentation(enabled: boolean, authentication?: { signedIn: boolean | null; message: string } | null) {
  if (!enabled) return { label: "Disabled", warning: false };
  if (authentication?.signedIn === true) return { label: "Signed in", warning: false };
  if (authentication?.signedIn === false) return { label: "Not signed in", warning: true };
  return { label: !authentication || authentication.message.startsWith("Checking") ? "Checking sign-in…" : "Could not check sign-in", warning: false };
}
