import { useEffect, useRef, useState, type FormEvent } from "react";
import { useLocation } from "react-router";
import { api, ApiError } from "../api";
import { Icon, Notice, PageHead, Talia } from "../ui";

interface Turn { role: "user" | "assistant"; content: string }

const suggestions = ["¿Cuánto vendí hoy?", "¿Qué cajera tuvo faltante esta semana?", "¿Qué fue lo más vendido este mes?"];

/** Chat with the owner's assistant. History lives only in this screen; a question asked from the "Pregúntale a Talia" bar arrives in router state. */
export function AssistantPage() {
  const { state } = useLocation() as { state: { question?: string } | null };
  const [history, setHistory] = useState<Turn[]>([]);
  const [question, setQuestion] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const asked = useRef(false);

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
    if (state?.question && !asked.current) {
      asked.current = true;
      ask(state.question);
    }
  }, [state]);

  const submit = (e: FormEvent) => (e.preventDefault(), ask(question));
  // Not configured or unavailable is not the owner's fault: amber, like being offline.
  const amber = error && (error.offline || error.status === 503);

  return (
    <>
      <PageHead title="Pregúntale a Talia" subtitle="Contesto con los datos que envían sus tiendas: ventas, cuadres y productos." />

      <section className="esc-card esc-chat" aria-live="polite">
        {history.length === 0 && !busy && (
          <div className="esc-empty" style={{ padding: "24px 0" }}>
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
            {/* Plain text: React escapes it, so an answer can never inject HTML. */}
            <p className={`esc-bubble esc-bubble--${t.role}`}>{t.content}</p>
          </div>
        ))}
        {busy && (
          <div className="esc-bubble-row">
            <Talia size={28} mood="thinking" />
            <p className="esc-bubble esc-bubble--assistant esc-muted">Buscando en sus datos…</p>
          </div>
        )}
      </section>

      {error && <Notice kind={amber ? "warning" : "error"} title={error.detail} />}

      <form className="esc-composer" onSubmit={submit}>
        <label className="esc-visually-hidden" htmlFor="question">
          Su pregunta
        </label>
        <input id="question" maxLength={1000} placeholder="Escriba su pregunta" value={question} onChange={(e) => setQuestion(e.target.value)} />
        <button type="submit" className="esc-button esc-button--primary" disabled={busy || !question.trim()}>
          <Icon name="send" size={18} /> Preguntar
        </button>
      </form>
    </>
  );
}
