import { lazy, Suspense, useState } from "react";
import { TaliaPopup } from "../../TaliaPopup";

// The chat loads the first time someone opens Talia: the site's pages (and their LCP) never wait for it.
const AssistantChat = lazy(() => import("../AssistantPage").then((m) => ({ default: m.AssistantChat })));

/** TK-315: Talia on every page of abastalia.com, for anyone, with the public knowledge base (POST /api/public/assistant/ask). Inside .esc-admin
 *  so the chat and the popup have the panel's tokens and look the same; the button sits bottom-right (the site has no bottom bar). */
export function SiteTalia() {
  const [open, setOpen] = useState(false);
  const [opened, setOpened] = useState(false);
  const show = () => (setOpened(true), setOpen(true));
  return (
    <div className="esc-admin site-talia">
      <TaliaPopup open={open} onOpen={show} onClose={() => setOpen(false)} subtitle="Asistente automático de Abastalia">
        {/* TK-315: the subtitle is the notice that the answers are automatic; the note under the chat gives the way to a person. */}
        {opened && (
          <Suspense fallback={<p className="talia-pop__chat esc-muted">Cargando el chat…</p>}>
            {/* A link in an answer: the site's pages are behind the sheet on a phone, so it closes to show them. */}
            <AssistantChat bot="public" inputId="site-talia-question" inPopup onNavigate={() => setOpen(false)} />
          </Suspense>
        )}
      </TaliaPopup>
    </div>
  );
}
