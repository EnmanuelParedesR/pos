import { useEffect, useRef, useState, type FormEvent } from "react";
import { api, ApiError, useApi, type ConnectionCode, type Store, type StoreSyncStatus } from "../api";
import { usePanel } from "../App";
import { ago, isStale } from "../format";
import { ConfirmButton, EmptyState, Icon, LoadError, Notice, PageHead, Skeleton } from "../ui";
import { UsageCard } from "./UsageCard";

export function StoresPage() {
  const { reloadStores, canEdit } = usePanel();
  const status = useApi<StoreSyncStatus[]>("/api/panel/sync-status");
  const [name, setName] = useState("");
  const [adding, setAdding] = useState(false);
  const [nameError, setNameError] = useState<string | null>(null);
  const [error, setError] = useState<ApiError | null>(null);

  async function addStore(e: FormEvent) {
    e.preventDefault();
    setError(null);
    if (!name.trim()) return setNameError("Escriba el nombre de la tienda.");
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

  // A new register shows up here as soon as it connects (TK-170).
  const connected = () => {
    status.reload();
    reloadStores();
  };

  return (
    <>
      <PageHead title="Tiendas" subtitle="Sus tiendas y las cajas que envían datos a este panel.">
        {canEdit && !adding && (
          <button type="button" className="esc-button esc-button--primary" onClick={() => setAdding(true)}>
            <Icon name="plus" size={18} /> Nueva tienda
          </button>
        )}
      </PageHead>

      {adding && (
        <form className="esc-card esc-setting" onSubmit={addStore} noValidate>
          <div className="esc-setting__about">
            <h2>Nueva tienda</h2>
            <p>Después conecte su caja con un código.</p>
          </div>
          <div className="esc-setting__body">
            <label className="esc-field">
              <span>Nombre de la tienda</span>
              <input
                className="esc-input"
                maxLength={100}
                autoFocus
                value={name}
                aria-invalid={nameError ? true : undefined}
                onChange={(e) => (setName(e.target.value), setNameError(null))}
                placeholder="Carnicería La Esquina"
              />
              {nameError && (
                <span className="esc-field__error" role="alert">
                  <Icon name="alert" size={14} />
                  {nameError}
                </span>
              )}
            </label>
            {error && <Notice kind={error.offline ? "warning" : "error"} title={error.detail} />}
            <div className="esc-form-actions">
              <button type="submit" className="esc-button esc-button--primary">
                Guardar
              </button>
              <button type="button" className="esc-button esc-button--ghost" onClick={() => (setAdding(false), setError(null), setNameError(null))}>
                Cancelar
              </button>
            </div>
          </div>
        </form>
      )}

      <LoadError error={status.error} />
      {status.loading && !status.data && (
        <div className="esc-card esc-card--padded store">
          <Skeleton width={220} height={24} />
          <Skeleton height={52} />
        </div>
      )}
      {status.data?.length === 0 && !adding && (
        <div className="esc-card">
          <EmptyState icon="store" title="Todavía no tiene tiendas">
            {canEdit ? "Cree la primera con “Nueva tienda” y conecte su caja." : "El dueño del negocio todavía no creó ninguna."}
          </EmptyState>
        </div>
      )}
      {status.data?.map((s) => <StoreCard key={s.storeId} store={s} canEdit={canEdit} onPoll={status.reload} onConnected={connected} />)}
      {canEdit && <UsageCard />}
    </>
  );
}

function StoreCard({ store: s, canEdit, onPoll, onConnected }: { store: StoreSyncStatus; canEdit: boolean; onPoll: () => void; onConnected: () => void }) {
  return (
    <section className="esc-card esc-card--padded store">
      <div className="store__head">
        <span className="store__icon">
          <Icon name="store" />
        </span>
        <div style={{ flex: "1 1 auto", minWidth: 0 }}>
          <h2 className="store__name">{s.storeName}</h2>
          <span className="esc-muted">
            {s.nodes.length === 0 ? "Ninguna caja conectada" : `${s.nodes.length} ${s.nodes.length === 1 ? "caja conectada" : "cajas conectadas"}`}
          </span>
        </div>
        {s.nodes.length > 0 && <SyncPill utc={s.lastSyncUtc} />}
      </div>
      {s.nodes.length > 0 && (
        <ul className="nodes">
          {s.nodes.map((n) => (
            <li key={n.nodeId}>
              <span className="esc-avatar" aria-hidden="true" style={{ background: "var(--surface-2)", color: "var(--text-2)" }}>
                <Icon name="register" size={16} />
              </span>
              <span style={{ flex: "1 1 auto", fontWeight: 600 }}>{n.name}</span>
              {/* R10 #5: still on the shared secret from before TK-241; it moves to its own key on its next upload. */}
              {n.usesSecret && <span className="esc-muted" title="Pasa a su propia llave al subir datos con Toki actualizado.">Con clave compartida</span>}
              <SyncPill utc={n.lastSyncUtc} />
              {/* TK-241: a replaced or lost PC stops sending; to use it again, connect it with a new code. */}
              {canEdit && (
                <ConfirmButton label="Quitar" danger confirm="Esa caja deja de enviar datos." onConfirm={() => api(`/api/panel/nodes/${n.nodeId}/revoke`, "POST").then(onConnected)} />
              )}
            </li>
          ))}
        </ul>
      )}
      {s.pendingEvents > 0 && <p className="esc-muted">{s.pendingEvents} datos recibidos por procesar.</p>}
      {s.failedEvents > 0 && <Notice kind="warning" title={`${s.failedEvents} datos recibidos no se pudieron procesar.`} />}
      {/* TK-217: a ReadOnly member sees the stores but cannot connect registers. */}
      {canEdit && <ConnectRegister storeId={s.storeId} nodes={s.nodes.length} onPoll={onPoll} onConnected={onConnected} />}
    </section>
  );
}

function SyncPill({ utc }: { utc: string | null }) {
  return <span className={`esc-pill esc-pill--${isStale(utc) ? "warning" : "success"}`}>{utc ? `Envió ${ago(utc)}` : "Sin datos todavía"}</span>;
}

/** "Conectar una caja": an 8-digit, single-use code that lasts 30 minutes. While it shows, the list checks every 5 s for the new register. */
function ConnectRegister({ storeId, nodes, onPoll, onConnected }: { storeId: string; nodes: number; onPoll: () => void; onConnected: () => void }) {
  const [code, setCode] = useState<ConnectionCode | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [now, setNow] = useState(Date.now());
  const [joined, setJoined] = useState(false);
  const nodesAtCode = useRef(nodes);

  useEffect(() => {
    if (!code) return;
    const timer = setInterval(() => setNow(Date.now()), 1000);
    const poll = setInterval(onPoll, 5000);
    return () => (clearInterval(timer), clearInterval(poll));
  }, [code, onPoll]);

  // The register used the code: hide it and say so.
  useEffect(() => {
    if (code && nodes > nodesAtCode.current) {
      setCode(null);
      setJoined(true);
      onConnected();
    }
  }, [code, nodes, onConnected]);

  async function create() {
    setError(null);
    setJoined(false);
    try {
      nodesAtCode.current = nodes;
      setCode(await api<ConnectionCode>(`/api/stores/${storeId}/connection-codes`, "POST"));
      setNow(Date.now());
    } catch (err) {
      setError(err as ApiError);
    }
  }

  // shortcut: the countdown uses this device's clock; a phone that is minutes off shows the wrong time left (the server still checks).
  const left = code ? Math.max(0, Math.floor((Date.parse(code.expiresUtc) - now) / 1000)) : 0;

  return (
    <div className="esc-col" style={{ gap: 12 }}>
      {error && <Notice kind={error.offline ? "warning" : "error"} title={error.detail} />}
      {joined && <Notice kind="success" title="Caja conectada. Sus ventas empiezan a llegar a este panel." />}
      {code && left > 0 ? (
        <div className="esc-code" role="status">
          <div className="esc-col" style={{ gap: 4 }}>
            <span className="esc-label">Código de conexión</span>
            <strong className="esc-code__digits" aria-label={code.code.split("").join(" ")}>
              {code.code.slice(0, 4)} {code.code.slice(4)}
            </strong>
          </div>
          <div className="esc-col" style={{ gap: 4, flex: "1 1 220px" }}>
            <span>En la PC principal de la tienda: Configuración → Nube → escriba este código.</span>
            <span className="esc-muted">
              Vence en {Math.floor(left / 60)}:{String(left % 60).padStart(2, "0")} · sirve una sola vez · esperando la caja…
            </span>
          </div>
        </div>
      ) : (
        <>
          {code && <Notice kind="warning" title="El código venció. Pida uno nuevo." />}
          <div>
            <button type="button" className="esc-button" onClick={create}>
              <Icon name="plus" size={18} /> Conectar una caja
            </button>
          </div>
        </>
      )}
    </div>
  );
}
