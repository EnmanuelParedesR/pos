import { useEffect, useState } from "react";
import { apiUrl } from "./api";

/** TK-255: the code from /precios?ref=CODE, kept in this browser until the business's first checkout sends it (the cloud keeps it from then on). */
const key = "toki.ref";

/** Toki's codes: 8 letters and digits without 0/O, 1/I/L. */
export const normalizeReferral = (code: string | null) => {
  const c = code?.trim().toUpperCase() ?? "";
  return /^[A-HJKMNP-Z2-9]{8}$/.test(c) ? c : null;
};

export function keptReferral(): string | null {
  try {
    return normalizeReferral(localStorage.getItem(key));
  } catch {
    return null;
  }
}

export function forgetReferral() {
  try {
    localStorage.removeItem(key);
  } catch {
    // storage blocked: nothing kept
  }
}

/** On /precios: a valid ?ref= is kept (checked with the cloud, limited per IP). True once it is. */
export function useReferralLink(): boolean {
  const [valid, setValid] = useState(false);
  useEffect(() => {
    const code = normalizeReferral(new URLSearchParams(window.location.search).get("ref"));
    if (!code) return;
    let alive = true;
    fetch(`${apiUrl}/api/public/referrals/${code}`, { headers: { Accept: "application/json" } })
      .then((r) => {
        if (!alive || !r.ok) return;
        try {
          localStorage.setItem(key, code);
        } catch {
          // storage blocked: the code is not carried
        }
        setValid(true);
      })
      .catch(() => undefined);
    return () => {
      alive = false;
    };
  }, []);
  return valid;
}
