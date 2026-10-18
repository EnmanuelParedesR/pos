import { useState, type FormEvent } from "react";
import { useNavigate } from "react-router";
import { api, ApiError, renewSession, roleNames, type Me, type MyTenant } from "../api";
import { Icon, Notice, PageHead } from "../ui";

/** TK-379: the businesses of this account. It opens another one of its own (each with its own subscription, plan and license) or moves to
 *  one it belongs to; the token is renewed so the whole panel follows (TK-217). Also reached from the phone, where the menu has no selector. */
export function BusinessesPage({ me, onMe }: { me: Me; onMe: (me: Me) => void }) {
  const navigate = useNavigate();
  const [adding, setAdding] = useState(false);
  const [name, setName] = useState("");
  const [nameError, setNameError] = useState<string | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [busy, setBusy] = useState(false);

  async function moveTo(work: () => Promise<unknown>) {
    setError(null);
    setBusy(true);
    try {
      await work();
      await renewSession();
      onMe(await api<Me>("/api/me"));
      navigate("/app");
    } catch (err) {
      setError(err as ApiError);
    } finally {
      setBusy(false);
    }
  }

  function add(e: FormEvent) {
    e.preventDefault();
    if (!name.trim()) return setNameError("Escriba el nombre del negocio.");
    void moveTo(() => api<MyTenant>("/api/tenants", "POST", { name: name.trim() }));
  }

  return (
    <>
      <PageHead title="Mis negocios" subtitle="Cambie de negocio o abra otro con la misma cuenta.">
        {!adding && (
          <button type="button" className="esc-button esc-button--primary" onClick={() => setAdding(true)}>
            <Icon name="plus" size={18} /> Agregar negocio
          </button>
        )}
      </PageHead>

      {adding && (
        <form className="esc-card esc-setting" onSubmit={add} noValidate>
          <div className="esc-setting__about">
            <h2>Agregar negocio</h2>
            <p>Otro RNC con su propia suscripción, su prueba de 14 días y su licencia. Usted queda como dueño.</p>
          </div>
          <div className="esc-setting__body">
            <label className="esc-field">
              <span>Nombre del negocio</span>
              <input
                className="esc-input"
                maxLength={100}
                autoFocus
                value={name}
                aria-invalid={nameError ? true : undefined}
                onChange={(e) => (setName(e.target.value), setNameError(null))}
                placeholder="Colmado La Esquina"
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
              <button type="submit" className="esc-button esc-button--primary" disabled={busy}>
                {busy ? "Abriendo…" : "Abrir el negocio"}
              </button>
              <button type="button" className="esc-button esc-button--ghost" onClick={() => (setAdding(false), setError(null), setNameError(null))}>
                Cancelar
              </button>
            </div>
          </div>
        </form>
      )}
      {!adding && error && <Notice kind={error.offline ? "warning" : "error"} title={error.detail} />}

      <ul className="esc-card businesses" aria-label="Negocios de esta cuenta">
        {me.tenants.map((t) => {
          const current = t.tenantId === me.tenantId;
          return (
            <li key={t.tenantId} className="businesses__item">
              <span className="esc-avatar" aria-hidden="true">
                {t.name.slice(0, 1).toUpperCase()}
              </span>
              <span className="businesses__name">
                <b>{t.name}</b>
                <span className="esc-muted">{roleNames[t.role]}</span>
              </span>
              {current ? (
                <span className="esc-pill esc-pill--success">Abierto</span>
              ) : (
                <button
                  type="button"
                  className="esc-button esc-button--small"
                  disabled={busy}
                  onClick={() => void moveTo(() => api("/api/me/tenant", "POST", { tenantId: t.tenantId }))}
                >
                  Abrir
                </button>
              )}
            </li>
          );
        })}
      </ul>
      <p className="esc-muted businesses__note">Cada negocio paga su propio plan; cambiar de negocio no cambia el de los demás.</p>
    </>
  );
}
