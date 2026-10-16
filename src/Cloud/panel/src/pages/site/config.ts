/** Public site settings (TK-228, was src/Site/site.json). Empty or null = still pending from the owner; the pages say "por confirmar". */
export const site = {
  /** The canonical origin, without a trailing slash (TK-259): canonical, og:url, og:image, JSON-LD and sitemap.xml are built from it. */
  url: "https://abastalia.com",
  /** Digits only, with the country code (1809…); with it, "Hablemos" opens WhatsApp. shortcut: no number yet (owner decision). */
  whatsapp: "",
  /** AbastaliaSetup.exe on the release feed (TK-242). shortcut: no hosting yet; /descargar shows "[URL DEL INSTALADOR]". */
  installerUrl: "",
  /** Plan and one-off prices (TK-203, TK-219). null = "Precio por confirmar"; never invent one. */
  prices: {
    /** By plan or add-on code (Essential, Default, MultiRegister, ExtraRegister…), shown until Paddle answers. Paddle is the source of truth. */
    byCode: {} as Record<string, string>,
    installation: null as string | null,
    certificationSupport: null as string | null,
  },
};

/** Where every call to action points: WhatsApp once there is a number, otherwise the contact box on Precios. */
export const contactHref = site.whatsapp
  ? `https://wa.me/${site.whatsapp}?text=${encodeURIComponent("Hola, quiero saber más de Abastalia")}`
  : "/precios#contacto";
