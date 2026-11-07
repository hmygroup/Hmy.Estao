export function connectionPresentation(enabled: boolean, authentication?: { signedIn: boolean | null; message: string; checking?: boolean } | null) {
  if (!enabled) return { key: "connection.disabled", warning: false };
  if (authentication?.signedIn === true) return { key: "connection.signed_in", warning: false };
  if (authentication?.signedIn === false) return { key: "connection.signed_out", warning: true };
  return { key: !authentication || authentication.checking ? "connection.checking" : "connection.unknown", warning: false };
}
