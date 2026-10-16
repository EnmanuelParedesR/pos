import { useState, type FormEvent } from "react";
import { api, ApiError, type ArchivedEcf } from "../api";
import { dateTime } from "../format";
import { Notice } from "../ui";

const statusNames: Record<string, string> = {
  Accepted: "Aceptado",
  AcceptedConditional: "Aceptado condicional",
  Rejected: "Rechazado",
  Pending: "En proceso",
  Simulated: "Simulado",
};

/** TK-207: the owner or the accountant gets an archived e-CF (signed XML and RFCE) back by its e-NCF, also after it moved to cold storage. */
export function EcfLookup() {
  const [encf, setEncf] = useState("");
  const [busy, setBusy] = useState(false);
  const [found, setFound] = useState<ArchivedEcf | null>(null);
  const [error, setError] = useState<ApiError | null>(null);

  async function search(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    setFound(null);
    try {
      setFound(await api<ArchivedEcf>(`/api/fiscal/archive/${encodeURIComponent(encf.trim().toUpperCase())}`));
    } catch (err) {
      setError(err as ApiError);
    } finally {
      setBusy(false);
    }
  }

  const download = (name: string, xml: string) => {
    const url = URL.createObjectURL(new Blob([xml], { type: "application/xml" }));
    const a = document.createElement("a");
    a.href = url;
    a.download = name;
    a.click();
    URL.revokeObjectURL(url);
  };

  return (
    <form className="esc-card esc-setting" onSubmit={search} aria-label="Comprobante electrónico">
      <div className="esc-setting__about">
        <h2>Buscar un comprobante electrónico</h2>
        <p>El XML firmado de cada e-CF se guarda 10 años en la nube. Escriba el e-NCF para descargarlo.</p>
      </div>
      <div className="esc-setting__body">
        <label className="esc-field">
          <span>e-NCF</span>
          <input className="esc-input" required pattern="[Ee][0-9]{12}" maxLength={13} placeholder="E310000000001" value={encf} onChange={(e) => setEncf(e.target.value)} />
        </label>
        <div className="esc-form-actions">
          <button type="submit" className="esc-button esc-button--primary" disabled={busy}>
            {busy ? "Buscando…" : "Buscar"}
          </button>
        </div>
        {error && <Notice kind={error.offline ? "warning" : "error"} title={error.detail} />}
        {found && !found.ready && (
          <Notice kind="info" title={`${found.eNcf} está en el archivo de largo plazo.`}>
            Ya pedimos sacarlo; tarda unas horas. Vuelva a buscarlo más tarde.
          </Notice>
        )}
        {found?.ready && found.signedXml && (
          <Notice kind="success" title={`${found.eNcf}: ${statusNames[found.status] ?? found.status} (${found.environment})`}>
            {found.signedUtc && <>Firmado el {dateTime(found.signedUtc)}. </>}
            {found.replacedByENcf && <>Reemplazado por {found.replacedByENcf}. </>}
            <span className="esc-row" style={{ gap: 8, marginTop: 8, flexWrap: "wrap" }}>
              <button type="button" className="esc-button esc-button--small" onClick={() => download(`${found.eNcf}.xml`, found.signedXml!)}>
                Descargar XML firmado
              </button>
              {found.rfceXml && (
                <button type="button" className="esc-button esc-button--small" onClick={() => download(`${found.eNcf}-RFCE.xml`, found.rfceXml!)}>
                  Descargar RFCE
                </button>
              )}
            </span>
          </Notice>
        )}
      </div>
    </form>
  );
}
