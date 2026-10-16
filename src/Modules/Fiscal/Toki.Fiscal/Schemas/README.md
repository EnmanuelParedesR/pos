# XSD oficiales de la DGII

Documentos públicos de la DGII, sin modificar y con su nombre original. Descargados el 15-10-2026 de la carpeta "Documentación Técnica (XSD)" de https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Paginas/documentacionSobreE-CF.aspx

| Archivo | URL | SHA-256 |
|---|---|---|
| `e-CF 31 v.1.0.xsd` | https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Documentaci%C3%B3n%20T%C3%A9cnica%20(XSD)/e-CF%2031%20v.1.0.xsd | `6F2909A93D84919518D2AE3C77FEAD4B35C3E8C95996B8AF67B0040C2E2BE298` |
| `e-CF 32 v.1.0.xsd` | https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Documentaci%C3%B3n%20T%C3%A9cnica%20(XSD)/e-CF%2032%20v.1.0.xsd | `151676DF03C61335B4AC1ECA97F99395488D57EA92793B49353602871E651F3C` |
| `e-CF 33 v.1.0.xsd` | https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Documentaci%C3%B3n%20T%C3%A9cnica%20(XSD)/e-CF%2033%20v.1.0.xsd | `087CD71BEBA11C19CF548E22B96363CB4AA210DC6F5C3C8A62122A42C88D2866` |
| `e-CF 34 v.1.0.xsd` | https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Documentaci%C3%B3n%20T%C3%A9cnica%20(XSD)/e-CF%2034%20v.1.0.xsd | `6C62EBABDEA9A6F6D62AE85D9E1C2EB788F44F3F0B61945FE116700C4EAAC2DA` |
| `RFCE 32 v.1.0.xsd` | https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Documentaci%C3%B3n%20T%C3%A9cnica%20(XSD)/RFCE%2032%20v.1.0.xsd | `6AAD535875661B05EEF072963295202FC94B3B320B257791FEB8D3C39A5F8EE6` |
| `ANECF v.1.0.xsd` | https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Documentaci%C3%B3n%20T%C3%A9cnica%20(XSD)/ANECF%20v.1.0.xsd | `AF2E6A16C2900DFA55264D6EBEC58CCBBBB25C1EB9821EE18CCC44FC142D0E78` |
| `Semilla v.1.0.xsd` | https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Documentaci%C3%B3n%20T%C3%A9cnica%20(XSD)/Semilla%20v.1.0.xsd | `BEBEF2033C930A30C8CEF45B513DB00403423125A7F3F48E100A73E3E355F67E` |
| `ARECF v1.0.xsd` | https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Documentaci%C3%B3n%20T%C3%A9cnica%20(XSD)/ARECF%20v1.0.xsd | `C6D186167159110959EB3F54706ECC7462AD9128FC2C3E03440C8352B1C66F10` |
| `ACECF v.1.0.xsd` | https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Documentaci%C3%B3n%20T%C3%A9cnica%20(XSD)/ACECF%20v.1.0.xsd | `072F65DE202DF8EC136A8D4493E0592172690A4E181047245401CC2E7B23C095` |

Los de RFCE, ANECF y Semilla se descargaron también el 15-10-2026, para TK-145, TK-150 y TK-144; el servidor de la DGII responde 403 sin un `User-Agent` de navegador. Van embebidos en `Toki.Fiscal` (`EcfXml.Validate`). `.gitattributes` los marca `-text` para que git no cambie sus finales de línea y el hash siga igual al del archivo de la DGII.

El acuse (`ARECF v1.0.xsd`, sin punto después de la v) y la aprobación comercial (`ACECF v.1.0.xsd`) se descargaron el 15-10-2026 para el receptor (TK-152); desde TK-246 viven aquí porque la tienda los firma, y la nube los enlaza en su `.csproj` (`FeXml.Validate`) junto a los de la 31 a la 34.
