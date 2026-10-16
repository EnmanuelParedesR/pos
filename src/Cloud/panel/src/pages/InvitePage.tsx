import { useEffect, useState, type FormEvent } from "react";
import { useLocation, useNavigate, useParams } from "react-router";
import { api, ApiError, hasSession, identityMessages, registerByInvite, renewSession, roleNames, signIn, signOut, type InvitePreview, type MemberRole } from "../api";
import { dateTime } from "../format";
import { Icon, Notice, Skeleton } from "../ui";
import { AuthFrame, PasswordField } from "./AuthFrame";

const roleLeads: Record<MemberRole, string> = {
  ReadOnly: "Podrá ver ventas, cuadres y comprobantes. No podrá cambiar nada.",
  Owner: "Podrá ver todo y administrar el negocio, como el dueño.",
};

/** /invite#token (TK-217; R8 #12: after "#" the token never reaches a server log; /invite/:token still opens older links): the invited person creates an account, or signs in, and joins the business. The link works once. TK-230: the sign-in design. */
export function InvitePage() {
  const params = useParams();
  const { hash } = useLocation();
  const token = hash.length > 1 ? decodeURIComponent(hash.slice(1)) : (params.token ?? "");
  const navigate = useNavigate();
  const invite = usePreview(token);
  const [signedIn, setSignedIn] = useState(hasSession);
  const [mode, setMode] = useState<"create" | "in">("create");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);

  const accept = () => api("/api/invites/accept", "POST", { token }).then(renewSession);

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
      navigate("/app"); // the panel loads the account (and the new business) itself
    } catch (err) {
      setError(err instanceof ApiError ? err : new ApiError(0, "Unknown", "No se pudo completar la operación."));
      setBusy(false);
    }
  }

  const switchTo = (m: "create" | "in") => (setMode(m), setError(null));
  const messages = error ? identityMessages(error) : [];
  const data = invite.data;

  return (
    <AuthFrame
      eyebrow="INVITACIÓN"
      title={data ? `Únase al equipo de ${data.tenantName}` : invite.error ? "Invitación no válida" : "Invitación"}
      lead={data ? roleLeads[data.role] : undefined}
    >
      {invite.loading && <Skeleton height={160} />}
      {invite.error && (
        <div className="auth-form">
          <Notice kind={invite.error.offline ? "warning" : "error"} title={invite.error.detail} />
          <a className="esc-button esc-button--primary auth-submit" href="/app">
            Ir al panel
          </a>
        </div>
      )}
      {data && (
        <form className="auth-form" onSubmit={join}>
          <div className="auth-field">
            <span className="auth-label" id="invite-email-label">
              Correo de la invitación
            </span>
            <div className="auth-readonly" aria-labelledby="invite-email-label" role="group">
              <span>{data.email}</span>
              <span className="auth-badge">{roleNames[data.role]}</span>
            </div>
          </div>
          {signedIn ? (
            <button type="button" className="auth-link auth-link--block" onClick={() => (signOut(), setSignedIn(false))}>
              Usar otra cuenta
            </button>
          ) : (
            <PasswordField
              id="invite-password"
              label={mode === "create" ? "Cree una contraseña" : "Contraseña"}
              value={password}
              onChange={setPassword}
              autoComplete={mode === "create" ? "new-password" : "current-password"}
              invalid={error !== null && !error.offline}
              describedBy={mode === "create" ? "invite-hint" : undefined}
            >
              {mode === "create" && (
                <span id="invite-hint" className="auth-hint">
                  8 caracteres o más, con una mayúscula, una minúscula y un número.
                </span>
              )}
            </PasswordField>
          )}
          {error && (
            <Notice kind={error.offline ? "warning" : "error"} title={messages[0]}>
              {messages.length > 1 ? messages.slice(1).join(" ") : undefined}
            </Notice>
          )}
          <button type="submit" className="esc-button esc-button--primary auth-submit" disabled={busy}>
            {busy ? "Un momento…" : mode === "in" && !signedIn ? "Entrar y unirme" : "Unirme al equipo"}
          </button>
          {!signedIn &&
            (mode === "create" ? (
              <p className="auth-note auth-note--big">
                ¿Ya tiene cuenta con este correo?{" "}
                <button type="button" className="auth-link" onClick={() => switchTo("in")}>
                  Entre y únase
                </button>
              </p>
            ) : (
              <p className="auth-note auth-note--big">
                ¿Todavía no tiene cuenta?{" "}
                <button type="button" className="auth-link" onClick={() => switchTo("create")}>
                  Cree una contraseña
                </button>
              </p>
            ))}
          <div className="auth-expiry">
            <Icon name="info" />
            <span>El enlace vence el {dateTime(data.expiresUtc)} y sirve una sola vez.</span>
          </div>
        </form>
      )}
    </AuthFrame>
  );
}

/** The invitation behind the token; POST so the token travels in the body (R8 #12). */
function usePreview(token: string) {
  const [state, setState] = useState<{ data?: InvitePreview; error?: ApiError; loading: boolean }>({ loading: true });
  useEffect(() => {
    let live = true;
    api<InvitePreview>("/api/invites/preview", "POST", { token }).then(
      (data) => live && setState({ data, loading: false }),
      (error) => live && setState({ error: error instanceof ApiError ? error : new ApiError(0, "Unknown", String(error)), loading: false }),
    );
    return () => {
      live = false;
    };
  }, [token]);
  return state;
}
