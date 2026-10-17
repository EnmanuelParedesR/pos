const key = "abastalia.preloadReload";

/** R12 #12 (TK-278): reloads after a missing lazy chunk at most once every 10 seconds per tab, so a chunk that keeps failing never loops. */
export function reloadOnce(storage: Pick<Storage, "getItem" | "setItem">, now: number, reload: () => void) {
  try {
    if (now - Number(storage.getItem(key) ?? 0) < 10_000) return;
    storage.setItem(key, String(now));
  } catch {
    return; // no storage: no automatic reload, the error shows instead
  }
  reload();
}
