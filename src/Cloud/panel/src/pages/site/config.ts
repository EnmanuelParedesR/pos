/** Public site settings (TK-228, was src/Site/site.json). Empty or null = still pending from the owner; the pages say "por confirmar". */
export const site = {
  /** The canonical origin, without a trailing slash (TK-259): canonical, og:url, og:image, JSON-LD and sitemap.xml are built from it. */
  url: "https://abastalia.com",
  /** Digits only, with the country code (1809…); with it, "Hablemos" opens WhatsApp. shortcut: no number yet (owner decision). */
  whatsapp: "18496313513",
  /** AbastaliaSetup.exe on the release feed (TK-242). shortcut: no hosting yet; /descargar shows "[URL DEL INSTALADOR]". */
  installerUrl: "",
  /** Plan and one-off prices (TK-203, TK-219). null = "Precio por confirmar"; never invent one. */
  prices: {
    /** TK-293, TK-313: monthly US$ by plan or add-on code (docs/cobro-paddle.md); annual = 10 × monthly. Shown while Paddle billing is off;
     * once it is on, only Paddle's count. */
    monthlyUsd: {
      Essential: 15,
      Default: 19,
      MultiRegister: 35,
      ExtraRegister: 6,
      ExtraCashier: 2,
      ExtraStore: 25,
      SupplierInvoiceReading: 5,
      AssistantPack: 5,
      ExtraUsers: 3,
    } as Record<string, number>,
    /** TK-313: pesos per US$1 for the RD$ amount the site shows first; Paddle charges the US$. Review it every quarter. */
    dopPerUsd: 61.5,
    installation: "Gratis por su cuenta o con ayuda remota. Visita a la tienda: US$45 (≈ RD$2,770)" as string | null,
    certificationSupport: "US$49 (≈ RD$3,015). Gratis con plan anual; gratis con cualquier plan hasta el 31/12/2026" as string | null,
    /** TK-313: the launch offer on /precios and /certificacion. shortcut: fixed text; set it to null on 1/1/2027. */
    launchPromo: "Acompañamiento gratis con cualquier plan pagado antes del 31/12/2026." as string | null,
  },
};

/** Where every call to action points: WhatsApp once there is a number, otherwise the contact box on Precios. */
export const contactHref = site.whatsapp
  ? `https://wa.me/${site.whatsapp}?text=${encodeURIComponent("Hola, quiero saber más de Abastalia")}`
  : "/precios#contacto";
