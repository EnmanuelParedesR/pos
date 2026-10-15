import { useEffect, useState, type FormEvent } from "react";
import { api, ApiError, useApi, type ConnectionCode, type Store, type StoreSyncStatus } from "../api";
import { usePanel } from "../App";
import { ago, isStale } from "../format";
import { Empty, LoadError, Notice } from "../ui";

export function StoresPage() {
  const { reloadStores } = usePanel();
  const status = useApi<StoreSyncStatus[]>("/api/panel/sync-status");
  const [name, setName] = useState("");
  const [adding, setAdding] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);

  async function addStore(e: FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      await api<Store>("/api/stores", "POST", { name: name.trim() });
      setName("");
      setAdding(false);
      status.reload();
      reloadStores();
    } catch (err) {
      setError(err as ApiError);
    }
  }

  return (
    <>
      <div className="page__head">
        <h1 className="esc-title">Tiendas</h1>
        {!adding && (
          <button type="button" className="esc-button esc-button--primary" onClick={() => setAdding(true)}>
            Nueva tienda
          </button>
        )}
      </div>

      {adding && (
        <form className="esc-card esc-card--padded esc-col" onSubmit={addStore}>
          <label className="esc-field">
            <span>Nombre de la tienda</span>
            <input className="esc-input" required maxLength={100} autoFocus value={name} onChange={(e) => setName(e.target.value)} />
          </label>
          {error && <Notice kind={error.offline ? "warning" : "error"} title={error.detail} />}
          <div className="esc-row">
            <button type="submit" className="esc-button esc-button--primary">
              Guardar
            </button>
            <button type="button" className="esc-button" onClick={() => (setAdding(false), setError(null))}>
              Cancelar
            </button>
          </div>
        </form>
      )}

      <LoadError error={status.error} />
      {status.data?.length === 0 && !adding && <Empty>Todavía no tiene tiendas. Cree la primera con “Nueva tienda”.</Empty>}
      {status.data?.map((s) => <StoreCard key={s.storeId} store={s} />)}
    </>
  );
}

function StoreCard({ store: s }: { store: StoreSyncStatus }) {
  return (
    <section className="esc-card esc-card--padded esc-col">
      <div className="esc-row">
        <h2 className="store__name">{s.storeName}</h2>
        <span className="esc-spacer" />
        {s.nodes.length > 0 && <SyncPill utc={s.lastSyncUtc} />}
      </div>
      {s.nodes.length === 0 ? (
        <p className="esc-muted">Ninguna caja conectada todavía.</p>
      ) : (
        <ul className="nodes">
          {s.nodes.map((n) => (
            <li key={n.nodeId} className="esc-row">
              <span>{n.name}</span>
              <span className="esc-spacer" />
              <SyncPill utc={n.lastSyncUtc} />
            </li>
          ))}
        </ul>
      )}
      {s.pendingEvents > 0 && <p className="esc-muted">{s.pendingEvents} datos recibidos por procesar.</p>}
      {s.failedEvents > 0 && <Notice kind="warning" title={`${s.failedEvents} datos recibidos no se pudieron procesar.`} />}
      <ConnectRegister storeId={s.storeId} />
    </section>
  );
}

function SyncPill({ utc }: { utc: string | null }) {
  return <span className={`esc-pill esc-pill--${isStale(utc) ? "warning" : "success"}`}>{utc ? `Actualizada ${ago(utc)}` : "Sin datos todavía"}</span>;
}

/** "Conectar una caja": an 8-digit, single-use code that lasts 30 minutes. */
function ConnectRegister({ storeId }: { storeId: string }) {
  const [code, setCode] = useState<ConnectionCode | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [now, setNow] = useState(Date.now());

  useEffect(() => {
    if (!code) return;
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [code]);

  async function create() {
    setError(null);
    try {
      setCode(await api<ConnectionCode>(`/api/stores/${storeId}/connection-codes`, "POST"));
      setNow(Date.now());
    } catch (err) {
      setError(err as ApiError);
    }
  }

  // shortcut: the countdown uses this device's clock; a phone that is minutes off shows the wrong time left (the server still checks).
  const left = code ? Math.max(0, Math.floor((Date.parse(code.expiresUtc) - now) / 1000)) : 0;

  return (
    <div className="esc-col">
      {error && <Notice kind={error.offline ? "warning" : "error"} title={error.detail} />}
      {code && left > 0 ? (
        <div className="code esc-col">
          <span className="esc-label">Código de conexión</span>
          <strong className="code__digits" aria-label={code.code.split("").join(" ")}>
            {code.code.slice(0, 4)} {code.code.slice(4)}
          </strong>
          <span className="esc-muted">
            Vence en {Math.floor(left / 60)}:{String(left % 60).padStart(2, "0")} · sirve una sola vez
          </span>
          <p>En la PC principal de la tienda: Configuración → Nube → escriba este código.</p>
        </div>
      ) : (
        <>
          {code && <Notice kind="warning" title="El código venció. Pida uno nuevo." />}
          <button type="button" className="esc-button" onClick={create}>
            Conectar una caja
          </button>
        </>
      )}
    </div>
  );
}
