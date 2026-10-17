import { useEffect, useRef, useState, type FormEvent, type ReactNode } from "react";
import { Link, useLocation } from "react-router";
import { api, ApiError } from "../api";
import { Icon, Notice, PageHead, Talia } from "../ui";

interface Turn { role: "user" | "assistant"; content: string }

/** A question asked from outside the chat (the "Pregúntale a Talia" bar). `id` tells two equal questions apart. */
export interface PendingQuestion { text: string; id: number }

/** TK-301: Talia names screens as plain relative paths. Only the panel's and these public routes become links; the rest stays text (never HTML or markdown). */
const pathPattern = /(?<=^|[\s(«"'¿¡])\/(?:app|precios|e-cf|certificacion|descargar|guia-e-cf|comparar|glosario)(?:\/[A-Za-z0-9-]+)*(?:#[A-Za-z0-9-]+)?(?![A-Za-z0-9/#-])/g;

export function withLinks(text: string, onNavigate?: () => void): ReactNode[] {
  const parts: ReactNode[] = [];
  let at = 0;
  for (const m of text.matchAll(pathPattern)) {
    parts.push(text.slice(at, m.index));
    parts.push(
      <Link key={m.index} to={m[0]} onClick={onNavigate}>
        {m[0]}
      </Link>,
    );
    at = m.index + m[0].length;
  }
  parts.push(text.slice(at));
  return parts;
}

const suggestions = ["¿Cuánto vendí hoy?", "¿Qué cajera tuvo faltante esta semana?", "¿Qué fue lo más vendido este mes?"];

/** The Asistente page. A question asked from elsewhere can arrive in router state. */
export function AssistantPage() {
  const { state } = useLocation() as { state: { question?: string } | null };
  return (
    <>
      <PageHead title="Pregúntale a Talia" subtitle="Contesto con los datos que envían sus tiendas: ventas, cuadres y productos." />
      <AssistantChat pending={state?.question ? { text: state.question, id: 0 } : null} />
    </>
  );
}

/** The chat itself: the Asistente page and Talia's popup (TK-300) both use it, so the API, quotas and limits are the same. History lives in this component. */
export function AssistantChat({ pending, inputId = "question", inPopup = false, onNavigate }: { pending?: PendingQuestion | null; inputId?: string; inPopup?: boolean; onNavigate?: () => void }) {
  const [history, setHistory] = useState<Turn[]>([]);
  const [question, setQuestion] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const asked = useRef<number | null>(null);
  const end = useRef<HTMLDivElement>(null);

  async function ask(text: string) {
    const q = text.trim();
    if (!q || busy) return;
    setBusy(true);
    setError(null);
    setQuestion("");
    setHistory((h) => [...h, { role: "user", content: q }]);
    try {
      const { answer } = await api<{ answer: string }>("/api/assistant/ask", "POST", { question: q, history });
      setHistory((h) => [...h, { role: "assistant", content: answer }]);
    } catch (err) {
      setError(err as ApiError);
      setQuestion(q);
      setHistory((h) => h.slice(0, -1));
    } finally {
      setBusy(false);
    }
  }

  useEffect(() => {
    if (pending && asked.current !== pending.id) {
      asked.current = pending.id;
      ask(pending.text);
    }
  }, [pending]);

  // The popup scrolls inside itself: keep the newest turn in view there.
  useEffect(() => {
    if (inPopup && history.length) end.current?.scrollIntoView?.({ block: "end" });
  }, [history.length, busy]);

  const submit = (e: FormEvent) => (e.preventDefault(), ask(question));
  // Not configured or unavailable is not the owner's fault: amber, like being offline.
  const amber = error && (error.offline || error.status === 503);

  return (
    <>
      <section className={inPopup ? "esc-chat talia-pop__chat" : "esc-card esc-chat"} aria-live="polite">
        {history.length === 0 && !busy && (
          <div className="esc-empty" style={{ padding: inPopup ? "8px 0" : "24px 0" }}>
            <Talia size={48} mood="happy" />
            <div className="esc-empty__title">¿Qué quiere saber hoy?</div>
            <div className="esc-row" style={{ justifyContent: "center", marginTop: 8 }}>
              {suggestions.map((s) => (
                <button key={s} type="button" className="esc-chip" onClick={() => ask(s)}>
                  {s}
                </button>
              ))}
            </div>
          </div>
        )}
        {history.map((t, i) => (
          <div key={i} className={`esc-bubble-row esc-bubble-row--${t.role}`}>
            {t.role === "assistant" && <Talia size={28} />}
            {/* Plain text: React escapes it, so an answer can never inject HTML. Only known in-app paths become links (TK-301). */}
            <p className={`esc-bubble esc-bubble--${t.role}`}>{t.role === "assistant" ? withLinks(t.content, onNavigate) : t.content}</p>
          </div>
        ))}
        {busy && (
          <div className="esc-bubble-row">
            <Talia size={28} mood="thinking" />
            <p className="esc-bubble esc-bubble--assistant esc-muted">Buscando en sus datos…</p>
          </div>
        )}
        <div ref={end} />
      </section>

      {error && <Notice kind={amber ? "warning" : "error"} title={error.detail} />}

      <form className="esc-composer" onSubmit={submit}>
        <label className="esc-visually-hidden" htmlFor={inputId}>
          Su pregunta
        </label>
        <input id={inputId} maxLength={1000} placeholder="Escriba su pregunta" value={question} onChange={(e) => setQuestion(e.target.value)} />
        <button type="submit" className="esc-button esc-button--primary" disabled={busy || !question.trim()}>
          <Icon name="send" size={18} /> Preguntar
        </button>
      </form>
    </>
  );
}
