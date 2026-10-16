import { useState } from "react";
import { ApiError, resendConfirmation, type Me } from "../api";
import { Notice } from "../ui";

/** TK-231: an unconfirmed e-mail gets no reset link ("¿La olvidó?"); accounts from before TK-230 never got the confirmation. One resend per
 * page load (the API also limits it per IP); the answer is the same whether or not anything was sent. */
export function ConfirmEmailBanner({ me }: { me: Me }) {
  const [state, setState] = useState<"idle" | "busy" | "sent" | "error">("idle");
  const [error, setError] = useState("");
  if (me.emailConfirmed !== false || !me.email) return null;

  async function resend() {
    setState("busy");
    try {
      await resendConfirmation(me.email!);
      setState("sent");
    } catch (err) {
      setError(
        err instanceof ApiError && err.status === 429
          ? "Ya pedimos varios correos. Espere unos minutos e intente de nuevo."
          : err instanceof ApiError
            ? err.detail
            : "No se pudo mandar el correo.",
      );
      setState("error");
    }
  }

  if (state === "sent")
    return (
      <Notice kind="success" title="Listo. Revise su correo.">
        Si su correo todavía necesita confirmarse, le llegará un enlace en unos minutos. Si no lo ve, busque en el correo no deseado.
      </Notice>
    );
  return (
    <Notice kind={state === "error" ? "warning" : "info"} title="Confirme su correo para poder recuperar la contraseña">
      {state === "error" ? error : `Abra el enlace que mandamos a ${me.email}.`}{" "}
      <button type="button" className="auth-link" disabled={state === "busy"} onClick={() => void resend()}>
        {state === "busy" ? "Mandando…" : "Reenviar correo"}
      </button>
    </Notice>
  );
}
