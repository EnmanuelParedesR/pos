import { useState, type ReactNode, type Ref } from "react";
import { Link } from "react-router";
import { Icon, Logo } from "../ui";
import { contactHref, site } from "./site/config";

/** TK-230: the frame of the pages without a session (Entrar / Crear cuenta, invitation, new password). Desktop: dark brand panel on the left,
 * the form on the right. Phone: a dark header with the logo and the headline, the form below; `extra` shows on desktop only. */
export function AuthFrame({ eyebrow, title, lead, extra, children }: { eyebrow?: string; title: string; lead?: ReactNode; extra?: ReactNode; children: ReactNode }) {
  return (
    <div className="esc-admin auth">
      <header className="auth__brand">
        <div className="auth__top">
          <Link to="/" className="auth__logo">
            <Logo size={36} variant="on-dark" />
          </Link>
          <Link to="/" className="auth__back auth__back--phone">
            Volver al sitio
          </Link>
        </div>
        <div className="auth__headline">
          {eyebrow && <span className="auth__eyebrow">{eyebrow}</span>}
          <h1>{title}</h1>
          {lead && <p>{lead}</p>}
        </div>
        {extra && <div className="auth__extra">{extra}</div>}
      </header>
      <main className="auth__main">
        <Link to="/" className="auth__back auth__back--wide">
          ← Volver al sitio
        </Link>
        <div className="auth__center">
          <div className="auth__card">{children}</div>
        </div>
        <p className="auth__help">
          ¿Necesita ayuda? <a href={contactHref}>{site.whatsapp ? "Escríbanos por WhatsApp" : "Escríbanos"}</a>
        </p>
      </main>
    </div>
  );
}

/** Password with a Mostrar / Ocultar button. `aside` sits right of the label ("¿La olvidó?"); `children` below the box (hint, checklist). */
export function PasswordField({
  id,
  label = "Contraseña",
  value,
  onChange,
  autoComplete,
  invalid,
  describedBy,
  aside,
  inputRef,
  children,
}: {
  id: string;
  label?: string;
  value: string;
  onChange: (value: string) => void;
  autoComplete: "current-password" | "new-password";
  invalid?: boolean;
  describedBy?: string;
  aside?: ReactNode;
  inputRef?: Ref<HTMLInputElement>;
  children?: ReactNode;
}) {
  const [show, setShow] = useState(false);
  return (
    <div className="auth-field">
      <div className="auth-field__head">
        <label htmlFor={id}>{label}</label>
        {aside}
      </div>
      <div className="auth-password">
        <input
          id={id}
          ref={inputRef}
          type={show ? "text" : "password"}
          required
          autoComplete={autoComplete}
          value={value}
          onChange={(e) => onChange(e.target.value)}
          aria-invalid={invalid || undefined}
          aria-describedby={describedBy}
        />
        <button type="button" aria-controls={id} onClick={() => setShow(!show)}>
          {show ? "Ocultar " : "Mostrar "}
          <span className="esc-visually-hidden">contraseña</span>
        </button>
      </div>
      {children}
    </div>
  );
}

/** The server's Identity rules (Program.cs: 8+, RequireUppercase/Lowercase/Digit; Identity counts ASCII letters and digits only). */
export const passwordChecks: [string, (password: string) => boolean][] = [
  ["8 caracteres o más", (p) => p.length >= 8],
  ["Una mayúscula", (p) => /[A-Z]/.test(p)],
  ["Una minúscula", (p) => /[a-z]/.test(p)],
  ["Un número", (p) => /[0-9]/.test(p)],
];

/** Live checklist while the person types a new password: icon and words, never color alone. */
export function PasswordChecklist({ id, password }: { id: string; password: string }) {
  return (
    <ul id={id} className="auth-checks" aria-label="La contraseña necesita">
      {passwordChecks.map(([text, ok]) => {
        const met = ok(password);
        return (
          <li key={text} className={met ? "is-met" : undefined}>
            <Icon name={met ? "check" : "minus"} size={16} />
            {text}
            <span className="esc-visually-hidden">{met ? ": listo" : ": falta"}</span>
          </li>
        );
      })}
    </ul>
  );
}
