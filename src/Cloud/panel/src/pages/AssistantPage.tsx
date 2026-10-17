import { useEffect, useRef, useState, type FormEvent, type ReactNode } from "react";
import { Link, useLocation, useNavigate } from "react-router";
import { api, ApiError, publicApi } from "../api";
import { Icon, Notice, PageHead, Talia } from "../ui";
import assistantRoutes from "./assistantRoutes.json";

/** TK-339: `navigation` is a route Talia suggests opening; the server only sends one of assistantRoutes.json. */
interface Turn { role: "user" | "assistant"; content: string; rated?: "up" | "down"; navigation?: string | null }

/** The button's label for a route Talia suggested; anything outside the shared list gets no button. */
export const routeLabel = (route?: string | null) => assistantRoutes.find((r) => r.route === route)?.label;

/** A question asked from outside the chat (the "Pregúntale a Talia" bar). `id` tells two equal questions apart. */
export interface PendingQuestion { text: string; id: number }

/** TK-301: Talia names screens as plain relative paths. Only the panel's and these public routes become links; the rest stays text (never HTML or markdown).
 *  TK-347: the same list as KnownPath in tests/Abastalia.Cloud.Tests/Talia/TaliaKnowledgeTests.cs (the knowledge base links only to these). */
// R12 #12: the prefix is a captured group, not a lookbehind (Safari before 16.4 cannot even parse one, and the whole bundle would fail).
const pathPattern = /(^|[\s(«"'¿¡])(\/(?:app|precios|e-cf|certificacion|descargar|guia-e-cf|comparar|glosario|novedades|terminos|privacidad|carniceria|colmado|deli)(?:\/[A-Za-z0-9-]+)*(?:#[A-Za-z0-9-]+)?)(?![A-Za-z0-9/#-])/g;

export function withLinks(text: string, onNavigate?: () => void): ReactNode[] {
  const parts: ReactNode[] = [];
  let at = 0;
  for (const m of text.matchAll(pathPattern)) {
    const start = m.index + m[1].length;
    parts.push(text.slice(at, start));
    parts.push(
      <Link key={start} to={m[2]} onClick={onNavigate}>
        {m[2]}
      </Link>,
    );
    at = start + m[2].length;
  }
  parts.push(text.slice(at));
  return parts;
}

/** TK-315: the same chat for two Talias. `panel` answers with the business's data (signed in); `public` is the site's, for anyone, with the public
 *  knowledge base only. Each has its endpoints, suggestions and wording. */
export type TaliaBot = "panel" | "public";

const bots = {
  panel: {
    ask: (body: unknown) => api<{ answer: string; navigation?: string | null }>("/api/assistant/ask", "POST", body),
    rate: (body: unknown) => api<void>("/api/assistant/feedback", "POST", body),
    suggestions: ["¿Cuánto vendí hoy?", "¿Qué cajera tuvo faltante esta semana?", "¿Qué fue lo más vendido este mes?"],
    title: "¿Qué quiere saber hoy?",
    busy: "Buscando en sus datos…",
  },
  public: {
    ask: (body: unknown) => publicApi<{ answer: string; navigation?: string | null }>("/api/public/assistant/ask", "POST", body),
    rate: (body: unknown) => publicApi<void>("/api/public/assistant/feedback", "POST", body),
    suggestions: ["¿Cuánto cuesta?", "¿Funciona sin internet?", "¿Me sirve para un colmado?", "¿Qué necesito para el e-CF?"],
    title: "Hola, soy Talia. ¿En qué le ayudo?",
    busy: "Buscando la respuesta…",
  },
} as const;

/** The Asistente page. A question asked from elsewhere can arrive in router state. */
export function AssistantPage() {
  const { state, pathname } = useLocation() as { state: { question?: string } | null; pathname: string };
  const navigate = useNavigate();
  return (
    <>
      <PageHead title="Pregúntale a Talia" subtitle="Contesto con los datos que envían sus tiendas y con lo que sé de Abastalia." />
      {/* R12 #12: asked once; coming back to this page (history) does not ask it again. */}
      <AssistantChat pending={state?.question ? { text: state.question, id: 0 } : null} onAsked={() => navigate(pathname, { replace: true, state: null })} />
    </>
  );
}

/** The chat itself: the Asistente page, Talia's popup in the panel (TK-300) and the site's (TK-315) use it. History lives in this component. */
/** `onAsked`: the pending question was sent; the caller clears it, so a remount does not ask it again (R12 #12). */
export function AssistantChat({ pending, inputId = "question", inPopup = false, onNavigate, onAsked, bot = "panel" }: { pending?: PendingQuestion | null; inputId?: string; inPopup?: boolean; onNavigate?: () => void; onAsked?: () => void; bot?: TaliaBot }) {
  const config = bots[bot];
  const [history, setHistory] = useState<Turn[]>([]);
  const [question, setQuestion] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const asked = useRef<number | null>(null);
  const end = useRef<HTMLDivElement>(null);
  const input = useRef<HTMLInputElement>(null);

  async function ask(text: string) {
    const q = text.trim();
    if (!q || busy) return;
    setBusy(true);
    setError(null);
    setQuestion("");
    setHistory((h) => [...h, { role: "user", content: q }]);
    try {
      const body = { question: q, history: history.map(({ role, content }) => ({ role, content })) };
      // TK-315: the site's Talia is told which page the visitor is on ("¿cuánto cuesta esto?" on /carniceria).
      const { answer, navigation } = await config.ask(bot === "public" ? { ...body, page: window.location.pathname } : body);
      setHistory((h) => [...h, { role: "assistant", content: answer, navigation }]);
    } catch (err) {
      setError(err as ApiError);
      setQuestion(q);
      setHistory((h) => h.slice(0, -1));
    } finally {
      setBusy(false);
    }
  }

  /** TK-349: 👍/👎 sends the question and the answer, and nothing else (the note under the chat says so). A failure is not worth an error. */
  function rate(index: number, helpful: boolean) {
    const answer = history[index];
    const asked = history[index - 1];
    if (answer.rated) return; // aria-disabled, not disabled: the button stays clickable
    setHistory((h) => h.map((t, i) => (i === index ? { ...t, rated: helpful ? "up" : "down" } : t)));
    config.rate({ helpful, question: asked?.content ?? null, answer: answer.content }).catch(() => undefined);
  }

  useEffect(() => {
    if (pending && asked.current !== pending.id) {
      asked.current = pending.id;
      ask(pending.text);
      onAsked?.();
    }
  }, [pending]);

  // The site's chat is lazy: when it mounts the popup has already focused its dialog, so on a computer the input takes the focus here.
  useEffect(() => {
    if (inPopup && window.matchMedia?.("(min-width: 761px)").matches) input.current?.focus();
  }, []);

  // The popup scrolls inside itself: keep the newest turn in view there.
  useEffect(() => {
    if (inPopup && history.length) end.current?.scrollIntoView?.({ block: "end" });
  }, [history.length, busy]);

  const submit = (e: FormEvent) => (e.preventDefault(), ask(question));
  // Not configured, resting or unavailable is not the person's fault: amber, like being offline.
  const amber = error && (error.offline || error.status === 503 || error.status === 429);

  return (
    <>
      <section className={inPopup ? "esc-chat talia-pop__chat" : "esc-card esc-chat"} aria-live="polite">
        {history.length === 0 && !busy && (
          <div className="esc-empty" style={{ padding: inPopup ? "8px 0" : "24px 0" }}>
            <Talia size={56} mood="happy" />
            <div className="esc-empty__title">{config.title}</div>
            <div className="esc-row" style={{ justifyContent: "center", marginTop: 8 }}>
              {config.suggestions.map((s) => (
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
            <div className="talia-answer">
              {/* Plain text: React escapes it, so an answer can never inject HTML. Only known in-app paths become links (TK-301). */}
              <p className={`esc-bubble esc-bubble--${t.role}`}>{t.role === "assistant" ? withLinks(t.content, onNavigate) : t.content}</p>
              {/* TK-339: Talia suggests, the person goes: never an automatic navigation. Under the answer, above its rating. */}
              {t.role === "assistant" && routeLabel(t.navigation) && (
                <Link className="esc-button talia-go" to={t.navigation!} onClick={onNavigate}>
                  Ir a {routeLabel(t.navigation)} <Icon name="chevron-right" size={18} />
                </Link>
              )}
              {/* TK-349: once rated both buttons stay (aria-disabled, the chosen one pressed), so the focus is not lost; the thanks follows them. */}
              {t.role === "assistant" && (
                <span className="talia-rate">
                  <button type="button" className="talia-rate__button" aria-label="Me sirvió" title="Me sirvió" aria-disabled={t.rated ? true : undefined} aria-pressed={t.rated ? t.rated === "up" : undefined} onClick={() => rate(i, true)}>
                    <Icon name="thumbs-up" size={16} />
                  </button>
                  <button type="button" className="talia-rate__button" aria-label="No me sirvió" title="No me sirvió" aria-disabled={t.rated ? true : undefined} aria-pressed={t.rated ? t.rated === "down" : undefined} onClick={() => rate(i, false)}>
                    <Icon name="thumbs-down" size={16} />
                  </button>
                  {t.rated && (
                    <span className="talia-rate__thanks">
                      {/* TK-315: a visitor Talia did not help is offered a person. */}
                      {bot === "public" && t.rated === "down" ? (
                        <>
                          Gracias. ¿Prefiere{" "}
                          <Link to="/precios#contacto" onClick={onNavigate}>
                            hablar con una persona
                          </Link>
                          ?
                        </>
                      ) : (
                        "Gracias, eso ayuda a mejorar a Talia."
                      )}
                    </span>
                  )}
                </span>
              )}
            </div>
          </div>
        ))}
        {busy && (
          <div className="esc-bubble-row">
            <Talia size={28} mood="thinking" />
            <p className="esc-bubble esc-bubble--assistant esc-muted">{config.busy}</p>
          </div>
        )}
        <div ref={end} />
      </section>

      {error && <Notice kind={amber ? "warning" : "error"} title={error.detail} />}

      <form className="esc-composer" onSubmit={submit}>
        <label className="esc-visually-hidden" htmlFor={inputId}>
          Su pregunta
        </label>
        <input ref={input} id={inputId} maxLength={bot === "public" ? 500 : 1000} placeholder="Escriba su pregunta" value={question} onChange={(e) => setQuestion(e.target.value)} />
        <button type="submit" className="esc-button esc-button--primary" disabled={busy || !question.trim()}>
          <Icon name="send" size={18} /> Preguntar
        </button>
      </form>
      <p className="talia-note">
        {/* TK-315: on the site the popup's subtitle says the answers are automatic; here, the way to a person. TK-349: what rating sends. */}
        {bot === "public" ? (
          <>
            Talia puede equivocarse. Al calificar se envían la pregunta y la respuesta.{" "}
            <Link to="/precios#contacto" onClick={onNavigate}>
              Hablar con una persona
            </Link>
            .
          </>
        ) : (
          "Talia puede equivocarse: revise lo importante. Al calificar se envían la pregunta y la respuesta."
        )}
      </p>
    </>
  );
}
