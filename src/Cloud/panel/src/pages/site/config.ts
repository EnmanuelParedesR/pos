/** Public site settings (TK-228, was src/Site/site.json). Empty or null = still pending from the owner; the pages say "por confirmar". */
export const site = {
  /** Domain without a trailing slash; with it, the pages get canonical and og:url. shortcut: no domain yet. */
  url: "",
  /** Digits only, with the country code (1809…); with it, "Hablemos" opens WhatsApp. shortcut: no number yet (owner decision). */
  whatsapp: "",
  /** Plan and one-off prices (TK-203, TK-219). null = "Precio por confirmar"; never invent one. */
  prices: {
    oneRegister: null as string | null,
    severalRegisters: null as string | null,
    severalStores: null as string | null,
    installation: null as string | null,
    certificationSupport: null as string | null,
  },
};

/** Where every call to action points: WhatsApp once there is a number, otherwise the contact box on Precios. */
export const contactHref = site.whatsapp
  ? `https://wa.me/${site.whatsapp}?text=${encodeURIComponent("Hola, quiero saber más de Toki")}`
  : "/precios#contacto";
