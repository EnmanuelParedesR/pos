# XSD oficiales de la DGII para el receptor (TK-152)

Documentos públicos de la DGII, sin modificar y con su nombre original. Descargados el 15-10-2026 de la carpeta "Documentación Técnica (XSD)" (mismo índice que `src/Modules/Fiscal/Toki.Fiscal/Schemas/README.md`).

| Archivo | URL | SHA-256 |
|---|---|---|
| `ARECF v1.0.xsd` | https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Documentaci%C3%B3n%20T%C3%A9cnica%20(XSD)/ARECF%20v1.0.xsd | `C6D186167159110959EB3F54706ECC7462AD9128FC2C3E03440C8352B1C66F10` |
| `ACECF v.1.0.xsd` | https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Documentaci%C3%B3n%20T%C3%A9cnica%20(XSD)/ACECF%20v.1.0.xsd | `072F65DE202DF8EC136A8D4493E0592172690A4E181047245401CC2E7B23C095` |

Ojo con los nombres: el del acuse es `ARECF v1.0.xsd` (sin punto después de la v) y el de la aprobación `ACECF v.1.0.xsd`. Los XSD de la 31 a la 34 se toman de `Toki.Fiscal/Schemas` (enlazados en el `.csproj` de la nube, no copiados). Van embebidos en `Toki.Host.Cloud` (`FeXml.Validate`).
