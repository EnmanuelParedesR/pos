import { useState, type FormEvent } from "react";
import { api, ApiError } from "../api";
import { Notice } from "../ui";

interface Turn { role: "user" | "assistant"; content: string }

const suggestions = ["¿Cuánto vendí hoy?", "¿Qué cajera tuvo faltante esta semana?", "¿Qué fue lo más vendido este mes?"];

/** Chat with the owner's assistant. History lives only in this screen. */
export function AssistantPage() {
  const [history, setHistory] = useState<Turn[]>([]);
  const [question, setQuestion] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);

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

  const submit = (e: FormEvent) => (e.preventDefault(), ask(question));
  // Not configured or unavailable is not the owner's fault: amber, like being offline.
  const amber = error && (error.offline || error.status === 503);

  return (
    <>
      <div className="page__head">
        <h1 className="esc-title">Asistente</h1>
      </div>

      <section className="chat esc-card esc-card--padded" aria-live="polite">
        {history.length === 0 && <p className="esc-muted">Pregunte por sus ventas, cuadres o productos. Contesto con los datos que envían sus tiendas.</p>}
        {history.map((t, i) => (
          // Plain text: React escapes it, so an answer can never inject HTML.
          <p key={i} className={`bubble bubble--${t.role}`}>
            {t.content}
          </p>
        ))}
        {busy && <p className="bubble bubble--assistant esc-muted">Buscando en sus datos…</p>}
      </section>

      {error && <Notice kind={amber ? "warning" : "error"} title={error.detail} />}

      <div className="esc-row">
        {suggestions.map((s) => (
          <button key={s} type="button" className="esc-chip" disabled={busy} onClick={() => ask(s)}>
            {s}
          </button>
        ))}
      </div>

      <form className="chat__form" onSubmit={submit}>
        <label className="esc-visually-hidden" htmlFor="question">
          Su pregunta
        </label>
        <input id="question" className="esc-input" maxLength={1000} placeholder="Escriba su pregunta" value={question} onChange={(e) => setQuestion(e.target.value)} />
        <button type="submit" className="esc-button esc-button--primary" disabled={busy || !question.trim()}>
          Preguntar
        </button>
      </form>
    </>
  );
}
