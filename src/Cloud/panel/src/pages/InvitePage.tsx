import { useState, type FormEvent } from "react";
import { useNavigate, useParams } from "react-router";
import { api, ApiError, hasSession, identityMessages, registerByInvite, renewSession, roleNames, signIn, signOut, useApi, type InvitePreview, type Me } from "../api";
import { Logo, Notice, Segmented, Skeleton } from "../ui";

/** /invite/:token (TK-217): the invited person creates an account, or signs in, and joins the business. The link works once. */
export function InvitePage({ onJoined }: { onJoined: (me: Me) => void }) {
  const { token = "" } = useParams();
  const navigate = useNavigate();
  const invite = useApi<InvitePreview>(`/api/invites/${encodeURIComponent(token)}`);
  const [signedIn, setSignedIn] = useState(hasSession);
  const [mode, setMode] = useState<"create" | "in">("create");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);

  const accept = () => api(`/api/invites/${encodeURIComponent(token)}/accept`, "POST").then(renewSession);

  async function join(e?: FormEvent) {
    e?.preventDefault();
    if (!invite.data) return;
    setBusy(true);
    setError(null);
    try {
      if (signedIn) await accept();
      else if (mode === "create") await registerByInvite(token, invite.data.email, password);
      else {
        await signIn(invite.data.email, password);
        await accept();
      }
      onJoined(await api<Me>("/api/me"));
      navigate("/");
    } catch (err) {
      setError(err instanceof ApiError ? err : new ApiError(0, "Unknown", "No se pudo completar la operación."));
      setBusy(false);
    }
  }

  const messages = error ? identityMessages(error) : [];

  return (
    <main className="esc-admin esc-signin">
      <form className="esc-card esc-signin__card" onSubmit={join}>
        <Logo size={40} />
        {invite.loading && <Skeleton height={80} />}
        {invite.error && (
          <>
            <h1>Invitación no válida</h1>
            <Notice kind={invite.error.offline ? "warning" : "error"} title={invite.error.detail} />
            <a className="esc-button esc-button--wide" href="/">
              Ir al panel
            </a>
          </>
        )}
        {invite.data && (
          <>
            <div>
              <h1>Únase a {invite.data.tenantName}</h1>
              <p className="esc-muted" style={{ marginTop: 4 }}>
                Le invitaron como <b>{roleNames[invite.data.role]}</b> con el correo <b>{invite.data.email}</b>.
              </p>
            </div>
            {signedIn ? (
              <button type="button" className="esc-button esc-button--ghost" onClick={() => (signOut(), setSignedIn(false))}>
                Usar otra cuenta
              </button>
            ) : (
              <>
                <Segmented<"create" | "in">
                  label="Opción"
                  options={[
                    ["create", "Crear cuenta"],
                    ["in", "Ya tengo cuenta"],
                  ]}
                  value={mode}
                  onChange={(m) => (setMode(m), setError(null))}
                />
                <label className="esc-field">
                  <span>Correo</span>
                  <input className="esc-input" type="email" value={invite.data.email} readOnly />
                </label>
                <label className="esc-field">
                  <span>Contraseña</span>
                  <input
                    className="esc-input"
                    type="password"
                    autoComplete={mode === "create" ? "new-password" : "current-password"}
                    required
                    minLength={mode === "create" ? 8 : undefined}
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                    aria-invalid={error ? true : undefined}
                  />
                  {mode === "create" && <span className="esc-field__hint">Mínimo 8 caracteres, con una mayúscula, una minúscula y un número.</span>}
                </label>
              </>
            )}
            {error && (
              <Notice kind={error.offline ? "warning" : "error"} title={messages[0]}>
                {messages.length > 1 ? messages.slice(1).join(" ") : undefined}
              </Notice>
            )}
            <button type="submit" className="esc-button esc-button--primary esc-button--wide" disabled={busy}>
              {busy ? "Un momento…" : signedIn ? `Unirme a ${invite.data.tenantName}` : mode === "create" ? "Crear cuenta y unirme" : "Entrar y unirme"}
            </button>
          </>
        )}
      </form>
    </main>
  );
}
