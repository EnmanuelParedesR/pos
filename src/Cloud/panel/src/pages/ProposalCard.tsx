import { useEffect, useId, useRef, useState } from "react";
import { api, ApiError } from "../api";
import { Icon } from "../ui";

/** TK-338: what `/api/assistant/ask` returns for a write Talia proposes. The summary is the server's, never the model's text. */
export interface Proposal { id: string; summary: string; expiresAt: string }

/** TK-338: what confirm and cancel return. `result` stays on the card: it never goes back to the model (docs/talia-acciones.md §3.6e). */
interface ActionResponse { status: "Proposed" | "Confirmed" | "Done" | "Failed" | "Cancelled" | "Expired"; result?: unknown; error?: string | null }

type State =
  | { kind: "pending"; notice?: string } // notice: a retryable problem (offline, 5xx, too many clicks); nothing changed on the server
  | { kind: "running" }
  | { kind: "done"; result?: unknown }
  | { kind: "failed" | "closed"; message: string }
  | { kind: "cancelled" | "expired" | "notAllowed" | "actionsOff" };

const expired = "Esta propuesta venció; pídala de nuevo.";

/** The card's state for a refused confirm or cancel (the codes are TK-338's, docs/talia-acciones.md §3). */
function fromError(e: ApiError): State {
  switch (e.code) {
    case "Assistant.ProposalExpired": return { kind: "expired" };
    case "Assistant.ProposalCancelled": return { kind: "cancelled" };
    case "Assistant.ActionsOff": return { kind: "actionsOff" };
    case "Assistant.NotAllowed": return { kind: "notAllowed" };
    case "Assistant.UnknownAction": return { kind: "failed", message: e.detail };
    case "Assistant.ProposalRunning":
    case "Assistant.ProposalClosed":
    case "Assistant.ProposalNotFound": return { kind: "closed", message: e.detail };
    default: return { kind: "pending", notice: e.detail };
  }
}

function fromResponse(r: ActionResponse): State {
  if (r.status === "Done") return { kind: "done", result: r.result };
  if (r.status === "Failed") return { kind: "failed", message: r.error || "No se pudo hacer." };
  if (r.status === "Cancelled") return { kind: "cancelled" };
  if (r.status === "Expired") return { kind: "expired" };
  return { kind: "pending" };
}

const minutesLeft = (expiresAt: string, now: number) => Math.ceil((Date.parse(expiresAt) - now) / 60_000);

/** TK-340: Talia's confirmation card. Only a click here confirms (never a "sí" typed in the chat), and only once. */
export function ProposalCard({ proposal }: { proposal: Proposal }) {
  const [state, setState] = useState<State>({ kind: "pending" });
  const [now, setNow] = useState(Date.now());
  const sending = useRef(false);
  const status = useRef<HTMLParagraphElement>(null);
  const confirm = useRef<HTMLButtonElement>(null);
  const title = useId();

  const left = minutesLeft(proposal.expiresAt, now);
  // shortcut: counts down with this device's clock; a clock far off ends it early or late, but the server still decides on confirm.
  const shown: State = state.kind === "pending" && left <= 0 ? { kind: "expired" } : state;
  const open = shown.kind === "pending" || shown.kind === "running";

  useEffect(() => {
    if (!open) return;
    const timer = setInterval(() => setNow(Date.now()), 15_000);
    return () => clearInterval(timer);
  }, [open]);

  async function send(verb: "confirm" | "cancel") {
    if (sending.current) return; // a double click sends once (the server is idempotent too)
    sending.current = true;
    setState({ kind: "running" });
    let next: State;
    try {
      next = fromResponse(await api<ActionResponse>(`/api/assistant/actions/${proposal.id}/${verb}`, "POST"));
    } catch (e) {
      next = fromError(e as ApiError);
    }
    sending.current = false;
    setState(next);
  }

  // Running disables the buttons, which drops the focus: put it back on Confirmar (to retry) or, once decided, on the outcome.
  const wasRunning = useRef(false);
  useEffect(() => {
    if (wasRunning.current && shown.kind !== "running") (shown.kind === "pending" ? confirm.current : status.current)?.focus();
    wasRunning.current = shown.kind === "running";
  }, [shown.kind]);

  return (
    <section className={`talia-card talia-card--${shown.kind}`} aria-labelledby={title}>
      <h3 id={title} className="talia-card__title">
        <Icon name="sparkles" size={18} /> {open ? "Talia propone" : "Propuesta de Talia"}
      </h3>
      <p className="talia-card__summary">{proposal.summary}</p>
      {/* The outcome is announced once; the countdown below is not (it ticks). */}
      <p ref={status} tabIndex={-1} role="status" className="talia-card__status">
        <Outcome state={shown} />
      </p>
      {open && (
        <div className="talia-card__actions">
          <button ref={confirm} type="button" className="esc-button esc-button--primary" disabled={shown.kind === "running"} onClick={() => send("confirm")}>
            Confirmar
          </button>
          <button type="button" className="esc-button esc-button--ghost" disabled={shown.kind === "running"} onClick={() => send("cancel")}>
            Cancelar
          </button>
          {shown.kind === "pending" && (
            <span className="talia-card__time" aria-live="off">
              <Icon name="clock" size={16} /> {left <= 1 ? "Vence en menos de 1 min" : `Vence en ${left} min`}
            </span>
          )}
        </div>
      )}
    </section>
  );
}

function Outcome({ state }: { state: State }) {
  switch (state.kind) {
    case "pending": return state.notice ? <span className="talia-card__bad">{state.notice}</span> : <>Nada cambia hasta que usted confirme.</>;
    case "running": return <>Haciéndolo…</>;
    // shortcut: only a text result is shown (TK-341's connection code should come back as one); other shapes just say "Hecho".
    case "done": return <span className="talia-card__good"><Icon name="check" size={16} />Hecho.{typeof state.result === "string" && <> <strong className="talia-card__result">{state.result}</strong></>}</span>;
    case "failed": return <span className="talia-card__bad">No se hizo: {state.message}</span>;
    case "closed": return <span className="talia-card__bad">{state.message}</span>;
    case "cancelled": return <>Cancelada. No se cambió nada.</>;
    case "expired": return <span className="talia-card__warn">{expired}</span>;
    case "notAllowed": return <span className="talia-card__bad">Usted ya no tiene permiso para hacer esto. Pídaselo al dueño del negocio.</span>;
    case "actionsOff": return <span className="talia-card__warn">Su plan no incluye que Talia haga cambios. Ella puede seguir contestando y llevándole a la pantalla donde se hace.</span>;
  }
}
