import { useEffect, useRef, useState, type FormEvent } from "react";
import { api, ApiError, download, useApi, type ConnectionCode, type SellingDevices, type Store, type StoreBackup, type StoreSyncStatus } from "../api";
import { usePanel } from "../App";
import { ago, dateTime, isStale } from "../format";
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

      <LiteDevice canEdit={canEdit} />
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
      {canEdit && (
        <ConnectWithCode
          path={`/api/stores/${s.storeId}/connection-codes`}
          button="Conectar una caja"
          where="En la PC principal de la tienda: Configuración → Nube → escriba este código."
          waiting="esperando la caja…"
          joined="Caja conectada. Sus ventas empiezan a llegar a este panel."
          count={s.nodes.length}
          onPoll={onPoll}
          onConnected={onConnected}
        />
      )}
      {/* TK-238: owners only; the API refuses the rest. */}
      {/* R12 #7: also with no register left (a stolen PC was just removed): that is when the owner needs them most. */}
      {canEdit && <StoreBackups storeId={s.storeId} />}
    </section>
  );
}

/** TK-321: a business the cloud serves sells from browsers (phone or computer), as many at a time as its plan allows (TK-347). The API
 *  answers 404 for a business with its own PC: then nothing shows. */
function LiteDevice({ canEdit }: { canEdit: boolean }) {
  const devices = useApi<SellingDevices>("/api/panel/lite/devices");
  if (devices.error?.status === 404 || (!devices.data && !devices.error)) return null;
  const list = devices.data?.devices ?? [];
  const allowed = devices.data?.allowed ?? 1;
  return (
    <section className="esc-card esc-card--padded store" aria-labelledby="lite-device">
      <div className="store__head">
        <span className="store__icon">
          <Icon name="register" />
        </span>
        <div style={{ flex: "1 1 auto", minWidth: 0 }}>
          <h2 className="store__name" id="lite-device">
            {allowed === 1 ? "Dispositivo de venta" : "Dispositivos de venta"}
          </h2>
          <span className="esc-muted">
            {allowed === 1
              ? "Se vende desde un celular o una computadora a la vez."
              : `Su plan permite ${allowed} celulares o computadoras a la vez; ${list.length} en uso.`}
          </span>
        </div>
      </div>
      <LoadError error={devices.error} />
      {list.length > 0 && (
        <ul className="nodes">
          {list.map((d) => (
            <li key={d.id}>
              <span className="esc-avatar" aria-hidden="true" style={{ background: "var(--surface-2)", color: "var(--text-2)" }}>
                <Icon name="register" size={16} />
              </span>
              <span className="esc-col" style={{ flex: "1 1 auto", gap: 0, minWidth: 0 }}>
                <span style={{ fontWeight: 600 }}>{d.name}</span>
                <span className="esc-muted">Unido el {dateTime(d.createdUtc)}</span>
              </span>
              {canEdit && (
                <ConfirmButton
                  label="Revocar"
                  danger
                  confirm="Deja de vender al instante y se cierran sus sesiones."
                  onConfirm={() => api(`/api/panel/lite/devices/${d.id}/revoke`, "POST").then(devices.reload)}
                />
              )}
            </li>
          ))}
        </ul>
      )}
      {list.length === 0 && !canEdit && <p className="esc-muted">Todavía no hay un dispositivo unido.</p>}
      {/* Design review: before the hint, so "unido" shows right under the device that joined. */}
      {canEdit && (
        <ConnectWithCode
          full={list.length >= allowed}
          path="/api/panel/lite/device-codes"
          button="Unir un dispositivo"
          where="En el celular o la computadora que va a vender, abra Abastalia Venta y escriba este código."
          waiting="esperando el dispositivo…"
          joined="Dispositivo unido. Ya puede vender con él."
          count={list.length}
          onPoll={devices.reload}
          onConnected={() => {}}
        />
      )}
      {list.length >= allowed && canEdit && (
        <p className="esc-muted">¿Perdió uno o lo cambia? Revóquelo aquí y una el nuevo con un código. Para más a la vez, suba de plan en Facturación.</p>
      )}
    </section>
  );
}

function SyncPill({ utc }: { utc: string | null }) {
  return <span className={`esc-pill esc-pill--${isStale(utc) ? "warning" : "success"}`}>{utc ? `Envió ${ago(utc)}` : "Sin datos todavía"}</span>;
}

/** A single-use, 8-digit code to join this panel (a store's register: 30 minutes; TK-321, a Lite selling device: 10). While it shows, the list
 *  checks every 5 s; when `count` grows, the code was used. `full`: no room for another one (the button hides; the notices stay). */
function ConnectWithCode({ path, button, where, waiting, joined: joinedText, count, full = false, onPoll, onConnected }: {
  path: string; button: string; where: string; waiting: string; joined: string; count: number; full?: boolean; onPoll: () => void; onConnected: () => void;
}) {
  const [code, setCode] = useState<ConnectionCode | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [now, setNow] = useState(Date.now());
  const [joined, setJoined] = useState(false);
  const countAtCode = useRef(count);

  useEffect(() => {
    if (!code) return;
    const timer = setInterval(() => setNow(Date.now()), 1000);
    const poll = setInterval(onPoll, 5000);
    return () => (clearInterval(timer), clearInterval(poll));
  }, [code, onPoll]);

  // The device used the code: hide it and say so.
  useEffect(() => {
    if (code && count > countAtCode.current) {
      setCode(null);
      setJoined(true);
      onConnected();
    }
  }, [code, count, onConnected]);

  async function create() {
    setError(null);
    setJoined(false);
    try {
      countAtCode.current = count;
      setCode(await api<ConnectionCode>(path, "POST"));
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
      {joined && <Notice kind="success" title={joinedText} />}
      {code && left > 0 ? (
        <div className="esc-code" role="status">
          <div className="esc-col" style={{ gap: 4 }}>
            <span className="esc-label">Código de conexión</span>
            <strong className="esc-code__digits" aria-label={code.code.split("").join(" ")}>
              {code.code.slice(0, 4)} {code.code.slice(4)}
            </strong>
          </div>
          <div className="esc-col" style={{ gap: 4, flex: "1 1 220px" }}>
            <span>{where}</span>
            <span className="esc-muted">
              Vence en {Math.floor(left / 60)}:{String(left % 60).padStart(2, "0")} · sirve una sola vez · {waiting}
            </span>
          </div>
        </div>
      ) : (
        <>
          {code && <Notice kind="warning" title="El código venció. Pida uno nuevo." />}
          {!full && (
            <div>
              <button type="button" className="esc-button" onClick={create}>
                <Icon name="plus" size={18} /> {button}
              </button>
            </div>
          )}
        </>
      )}
    </div>
  );
}

const megabytes = (bytes: number) => `${Math.max(0.1, bytes / 1024 / 1024).toLocaleString("es-DO", { maximumFractionDigits: 1 })} MB`;

/** TK-238: the store's backups the cloud keeps (7 daily + 4 weekly), encrypted with the owner's passphrase: restoring one needs it. */
function StoreBackups({ storeId }: { storeId: string }) {
  const backups = useApi<StoreBackup[]>(`/api/stores/${storeId}/backups`);
  const [error, setError] = useState<ApiError | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [all, setAll] = useState(false);

  async function save(b: StoreBackup) {
    setError(null);
    setBusy(b.id);
    try {
      await download(`/api/stores/${storeId}/backups/${b.id}`);
    } catch (err) {
      setError(err as ApiError);
    } finally {
      setBusy(null);
    }
  }

  return (
    <div className="esc-col" style={{ gap: 8 }}>
      <h3 className="store__section">Respaldos en la nube</h3>
      <LoadError error={backups.error} />
      {error && <Notice kind={error.offline ? "warning" : "error"} title={error.detail} />}
      {backups.data?.length === 0 && (
        <p className="esc-muted">Todavía no hay. Actívelos en la PC principal: Configuración → Respaldos → Copiar también a la nube.</p>
      )}
      {!!backups.data?.length && (
        <>
          <ul className="nodes">
            {(all ? backups.data : backups.data.slice(0, 3)).map((b) => (
              <li key={b.id}>
                <span className="esc-avatar" aria-hidden="true" style={{ background: "var(--surface-2)", color: "var(--text-2)" }}>
                  <Icon name="cloud" size={16} />
                </span>
                <span className="esc-col" style={{ flex: "1 1 auto", gap: 0, minWidth: 0 }}>
                  <span style={{ fontWeight: 600 }}>{dateTime(b.createdUtc)}</span>
                  <span className="esc-muted">{megabytes(b.bytes)}</span>
                </span>
                <button type="button" className="esc-button esc-button--small" disabled={busy !== null} onClick={() => save(b)}>
                  {busy === b.id ? "Descargando…" : "Descargar"}
                </button>
              </li>
            ))}
          </ul>
          {/* Design review: 11 rows per store pushed the other stores far down; the newest three are what an owner usually needs. */}
          {!all && backups.data.length > 3 && (
            <div>
              <button type="button" className="esc-button esc-button--ghost esc-button--small" onClick={() => setAll(true)}>
                Ver los {backups.data.length} respaldos
              </button>
            </div>
          )}
          <p className="esc-muted">Están cifrados con la frase de los respaldos: para restaurar uno en la PC necesita esa frase. Abastalia no la conoce.</p>
        </>
      )}
    </div>
  );
}
