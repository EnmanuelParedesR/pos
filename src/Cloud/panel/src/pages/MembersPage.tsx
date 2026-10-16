import { useState, type FormEvent } from "react";
import { api, ApiError, roleNames, useApi, type CreatedInvite, type Me, type MemberRole, type Members } from "../api";
import { dateTime, dayName } from "../format";
import { ConfirmButton, EmptyState, Icon, LoadError, Notice, PageHead, Segmented, Skeleton } from "../ui";

const roleOptions: [MemberRole, string][] = [
  ["ReadOnly", roleNames.ReadOnly],
  ["Owner", roleNames.Owner],
];

/** "Equipo" (TK-217, Owners only): who sees this business's panel. Invite with a single-use link, change roles, remove people. */
export function MembersPage({ me }: { me: Me }) {
  const list = useApi<Members>("/api/members");
  const [error, setError] = useState<ApiError | null>(null);

  const run = (call: Promise<unknown>) =>
    call.then(
      () => (setError(null), list.reload()),
      (err) => setError(err as ApiError),
    );

  return (
    <>
      <PageHead title="Equipo" subtitle={`Quién puede ver el panel de ${me.tenantName}. Solo lectura ve ventas, cuadres y tiendas, pero no cambia nada.`} />

      <InviteForm onInvited={list.reload} />

      <LoadError error={list.error} />
      {error && <Notice kind={error.offline ? "warning" : "error"} title={error.detail} />}
      {list.loading && !list.data && <Skeleton height={120} />}

      {list.data && (
        <section className="esc-card esc-card--padded" aria-label="Miembros">
          <h2 className="esc-card__title">Miembros</h2>
          <ul className="platform-kv">
            {list.data.members.map((m) => (
              <li key={m.userId} style={{ flexWrap: "wrap" }}>
                <div className="esc-col" style={{ gap: 2, minWidth: 0 }}>
                  <b style={{ overflowWrap: "anywhere" }}>
                    {m.email ?? "Sin correo"}
                    {m.email === me.email && " (usted)"}
                  </b>
                  <span className="esc-muted">Desde {dayName(m.joinedUtc.slice(0, 10))}</span>
                </div>
                <span className="esc-row" style={{ gap: 8, flexWrap: "wrap" }}>
                  <select
                    className="esc-select"
                    style={{ width: "auto" }}
                    aria-label={`Rol de ${m.email ?? "este miembro"}`}
                    value={m.role}
                    onChange={(e) =>
                      run(
                        api(`/api/members/${encodeURIComponent(m.userId)}/role`, "PUT", { role: e.target.value }).then(() => {
                          // Stepping down yourself: the panel reloads as Solo lectura.
                          if (m.email === me.email) window.location.assign("/");
                        }),
                      )
                    }
                  >
                    {roleOptions.map(([value, label]) => (
                      <option key={value} value={value}>
                        {label}
                      </option>
                    ))}
                  </select>
                  {/* TK-201: an Owner always sees cost and profit; a Solo lectura member (the accountant) only if allowed here. */}
                  {m.role === "ReadOnly" && (
                    <label className="esc-row" style={{ gap: 6 }}>
                      <input
                        type="checkbox"
                        checked={m.canViewCosts ?? false}
                        onChange={(e) => run(api(`/api/members/${encodeURIComponent(m.userId)}/costs`, "PUT", { canViewCosts: e.target.checked }))}
                      />
                      Ve costos y ganancias
                    </label>
                  )}
                  <ConfirmButton
                    label="Quitar"
                    danger
                    confirm="Ya no podrá entrar a este panel."
                    onConfirm={() => api(`/api/members/${encodeURIComponent(m.userId)}`, "DELETE").then(list.reload)}
                  />
                </span>
              </li>
            ))}
          </ul>
        </section>
      )}

      {list.data && (
        <section className="esc-card esc-card--padded" aria-label="Invitaciones pendientes">
          <h2 className="esc-card__title">Invitaciones pendientes</h2>
          {list.data.invites.length === 0 ? (
            <EmptyState icon="inbox" title="No hay invitaciones pendientes" />
          ) : (
            <ul className="platform-kv">
              {list.data.invites.map((i) => (
                <li key={i.id} style={{ flexWrap: "wrap" }}>
                  <div className="esc-col" style={{ gap: 2, minWidth: 0 }}>
                    <b style={{ overflowWrap: "anywhere" }}>{i.email}</b>
                    <span className="esc-muted">
                      {roleNames[i.role]} · vence {dateTime(i.expiresUtc)}
                    </span>
                  </div>
                  <ConfirmButton label="Cancelar invitación" confirm="El enlace deja de servir." onConfirm={() => api(`/api/members/invites/${i.id}`, "DELETE").then(list.reload)} />
                </li>
              ))}
            </ul>
          )}
        </section>
      )}
    </>
  );
}

function InviteForm({ onInvited }: { onInvited: () => void }) {
  const [email, setEmail] = useState("");
  const [role, setRole] = useState<MemberRole>("ReadOnly");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const [created, setCreated] = useState<CreatedInvite | null>(null);
  const [copied, setCopied] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    setCopied(false);
    try {
      setCreated(await api<CreatedInvite>("/api/members/invites", "POST", { email: email.trim(), role }));
      setEmail("");
      onInvited();
    } catch (err) {
      setError(err as ApiError);
    } finally {
      setBusy(false);
    }
  }

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(created!.link ?? "");
      setCopied(true);
    } catch {
      // No clipboard (old browser, no permission): the link stays selectable on screen.
    }
  };

  return (
    <form className="esc-card esc-setting" onSubmit={submit}>
      <div className="esc-setting__about">
        <h2>Invitar a alguien</h2>
        <p>Le damos un enlace que sirve una sola vez y vence en 7 días.</p>
      </div>
      <div className="esc-setting__body">
        <label className="esc-field">
          <span>Correo</span>
          <input className="esc-input" type="email" required maxLength={256} value={email} onChange={(e) => setEmail(e.target.value)} placeholder="contable@ejemplo.com" />
        </label>
        <Segmented<MemberRole> label="Rol" options={roleOptions} value={role} onChange={setRole} />
        {error && <Notice kind={error.offline ? "warning" : "error"} title={error.detail} />}
        <div className="esc-form-actions">
          <button type="submit" className="esc-button esc-button--primary" disabled={busy}>
            <Icon name="plus" size={18} /> {busy ? "Un momento…" : "Invitar"}
          </button>
        </div>
        {created &&
          (created.link === null ? (
            // R8 #1: with an e-mail provider the link goes only to the invited address.
            <Notice kind="success" title={`Le enviamos la invitación a ${created.invite.email}.`}>
              El enlace llega a ese correo y sirve una sola vez.
            </Notice>
          ) : (
            <Notice kind="success" title={`Invitación lista para ${created.invite.email}.`}>
              {/* shortcut: no e-mail is sent yet (no provider wired); the owner sends this link by WhatsApp or e-mail. */}
              Todavía no enviamos el correo: copie este enlace y envíeselo. <code style={{ overflowWrap: "anywhere" }}>{created.link}</code>{" "}
              <button type="button" className="esc-button esc-button--small" onClick={copy}>
                {copied ? "Copiado" : "Copiar enlace"}
              </button>
            </Notice>
          ))}
      </div>
    </form>
  );
}
