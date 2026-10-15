# White-Label POS for Dominican Retail: Project Context

> Handoff document summarizing decisions, research and open questions to date (October 12, 2026).
> Prices and regulatory details were checked in October 2026; re-verify before relying on them.

---

## 1. The idea

- **Product:** a white-label desktop **POS + inventory + cash management** system for Dominican retail food businesses (butcher shops first, then delis, groceries, colmados).
- **Business model:** monthly subscription with a base plan plus paid feature add-ons (cloud sync, multi-branch, AI, etc.). Resellers can rebrand it.
- **Market thesis:** local DR desktop POS software is generally poor (VB6/Access-era UX). Win on modern UX, offline reliability, vertical features and local support.
- **Developer profile:** C#/.NET and Azure background; main developer, AI-assisted.

---

## 2. Stack decision

| Layer | Choice | Notes |
|---|---|---|
| Desktop shell | **WPF + BlazorWebView** (Blazor Hybrid) | Windows-only, mature. MAUI avoided (Windows desktop reputation). |
| UI | MudBlazor or Fluent UI Blazor | HTML/CSS, so white-label theming = CSS variables. |
| Local backend | **ASP.NET Core** as a Windows service on the main PC | Registers talk to this API, never to SQL directly. |
| Local DB | **SQL Server Express** + EF Core | Free, 10 GB per DB. Listens on localhost only. |
| Cloud | Azure (Azure SQL multi-tenant, API, dashboard) | Paid tiers only. |
| Updates | Velopack | Silent auto-updates, code-signed. |

**Rejected / considered:**
- **Electron:** heavier (bundles Chromium + Node), two languages, awkward hardware access. No RAM advantage.
- **Alternative if React UI wanted:** React + Tauri UI over the same C# local API (better AI training coverage for UI, but two languages/pipelines).
- **Avalonia / native WPF:** lightest RAM option if very old hardware becomes the priority.

**Minimum OS:** Windows 10/11 64-bit (prefer 11). **Windows 7 is unsupported by every modern stack** (Electron, WebView2, .NET 7+). For dedicated registers, consider Windows 10 IoT Enterprise LTSC. Opportunity: sell preconfigured hardware bundles.

---

## 3. Architecture

```
Shop LAN (untrusted Wi-Fi assumed)
┌───────────────────────────────────────────┐
│ Main PC                                    │
│  - SQL Server Express (127.0.0.1 only)     │
│  - Local API (Windows service, HTTPS)      │
│  - Client .p12 cert (non-exportable)       │
│  - e-CF signing + offline queue            │
│ Registers (Caja 1..N)                      │
│  - Blazor Hybrid client, paired via cert   │
│ Hardware: scale, thermal printer, drawer,  │
│           barcode scanner, label printer   │
└──────────────────┬────────────────────────┘
                   │ (internet, when available)
         ┌─────────▼──────────┐
         │ Your Azure cloud    │──► e-CF provider ──► DGII
         │ licensing, sync,    │
         │ dashboard, AI       │
         └────────────────────┘
```

**Principles**
- **Local-first:** an internet outage changes nothing for sales, stock or cash.
- **Interfaces for everything external:** `IEcfProvider`, `IPaymentTerminal`, `IScale`, `IPrinter`, `IFeatureService`.
- **GUID keys from day one** (sync-ready).
- Sale = stock decrement + shift entry in **one DB transaction**.
- Verticals via configurable features, not forks.

**Setup wizard (in-app, first run)**
- **Main PC role:** silent install of SQL Express, DB creation, local API service, firewall rule (private profile, local subnet), license activation.
- **Register role:** discovers the main PC on the LAN, pairs with a one-time code + admin PIN, gets its own device certificate.

---

## 4. Security (assume hostile shared Wi-Fi)

1. SQL Server bound to localhost; no `sa` on the LAN.
2. Only **paired devices** reach the API (client certificates, revocable).
3. HTTPS with a wizard-generated cert, **pinned** by registers at pairing.
4. Cashier PINs with lockout; **server-side** role checks; append-only, hash-chained audit log.
5. Encrypted cloud backups (ransomware/disk failure); code-signed app and updates.
6. Card data never touches the app (bank terminal handles it).

**Residual risks:** malware on a PC, physical theft of the main PC, dishonest employees (mitigate with roles, audit logs, blind counts).

**Remote access**
- Owner access = **cloud dashboard via sync**, not a VPN.
- Multi-branch = each branch has its own local server + cloud sync (not a live tunnel).
- **Your remote support:** self-hosted **NetBird** (or Headscale) management server on a small cloud Linux VM; agent on each main PC.
  - Isolation per client via groups + setup keys + policies. **Delete NetBird's default allow-all policy on day one.**
  - Support group can reach all clients; clients never reach each other.
  - Tailscale is free only for personal use; business plans are per seat.

---

## 5. Functional scope

**Base (all plans)**
- POS checkout: scale (weight in **libras**), barcode, weighed-item EAN-13 labels (prefix 2), thermal printer, cash drawer
- Products, inventory (units + weight), purchases from suppliers (RNC), transfers, adjustments, physical counts, weighted-average cost
- Cash shifts: opening float, **blind count** at close, drops/petty cash, Z-report, supervisor PIN for voids/refunds/discounts
- Users, roles, audit log
- e-CF invoicing
- Customer credit accounts (limits, payment history)
- Payments: cash, card (manual), transfer, split; DOP/USD
- Basic reports, local backup

**Extensions**
- Extra registers
- Cloud backup + owner dashboard
- **Butcher module:** cut yields, shrinkage/trim loss, weighed labels
- Lots and expiry dates
- Advanced reports (606/607 export)
- Multi-branch sync
- AI assistant + WhatsApp alerts
- Integrated card terminal (v2)
- Priority remote support

**Build order:** products/inventory → POS + cash shifts → e-CF → reports.

---

## 6. Pricing v1 (draft, USD/month)

| Plan | Price | Includes |
|---|---|---|
| Básico | $25 | Base, 1 register |
| Pro | $45 | Base, 3 registers, cloud backup + dashboard, 1 vertical module |
| Premium | $79 | Pro + multi-branch, AI + WhatsApp, priority support |

Add-ons: extra register $8, extra branch $25, setup fee $100–150.

**Rough economics (assumptions to validate)**
- Variable cost per client: ~$8–15/month (e-CF, cloud, payment fees).
- Fixed: ~$1,300/month (support person, servers, code-signing, marketing), excluding founder salary.
- Average $40/client → break-even ≈ 45 clients; 100 clients ≈ $2,500–3,000/month profit.
- Resellers expect 20–30%.
- Many DR SMBs pay by bank transfer: plan manual confirmation + grace periods.
- Your own subscription invoices to clients also need e-CF + ITBIS (decide if prices include ITBIS).

**If e-CF is billed per company at $15** (5,000-invoice plan), prices likely need to rise to ~$35 Básico / ~$55 Pro. If pooled across clients, ~$6–8/client and the original pricing holds.

---

## 7. Competitor: Alegra

- Cloud/web-first (browser on PC, tablet, phone); POS has an **offline mode** that syncs later (likely PWA-style: service worker + IndexedDB).
- DR POS plans from ~$25 USD/month; PYME ~$39 with up to 2 warehouses; e-CF included (via **Alanube**, a certified provider).
- Has: hardware support (printers, scales, scanners, drawers), cash shifts, multi-branch, accounting, API.
- **Weaknesses of browser offline:** each device is an island while offline (no shared stock/totals), clearing browser data loses unsynced sales, some features unavailable offline.
- **Our edge:** local-first multi-register LAN ("if the internet goes down, nothing changes"), butcher-specific depth, speed, local hands-on support.

---

## 8. e-CF (electronic invoicing): critical

**Regulatory facts**
- Ley 32-23. Small, micro and non-classified taxpayers: deadline **November 15, 2026** (extended from May 15). Large Local and Medium taxpayers: e-CF type "E" only from **November 1, 2026**.
- Without e-CF there is essentially **no market** among formal businesses. You don't have to *build* it, but you must *support* it.
- Each client is the legal issuer: own RNC, own digital certificate (.p12 from Avansi, Digicert, etc.), own e-NCF sequences.

**Options**
1. **v1: integrate with a certified provider** (behind `IEcfProvider`).
2. **Later: become a PSFE** (authorized provider): company registered for software development, DGII application (answer in 10 business days), test set, sworn declaration. Only worth it at hundreds of clients.

**Providers evaluated**
- **ECF SSD** (ecf.ssd.com.do), preferred. Official .NET SDK `SSDDO.ECF_DGII.SDK` 4.0.0 (Apache-2.0, .NET Standard 2.0 / .NET 5–9, Kiota-based). `SendEcfAsync` returns status, `CodSec` (security code), signature date and `ImpresionUrl` (QR). Envs: Test / Cert / Prod. Supports migration from other providers. Young package (~4.4k downloads).
  - Pricing: $10 (1,000 invoices) / $15 (5,000) / $25 (10,000), extra $0.01 per invoice, 10% off annual. Enterprise option runs on-premise/hybrid (Docker/IIS).
  - Client onboarding: create company by RNC → upload .p12 → DGII test set (they automate most; client uploads files in DGII Oficina Virtual; 2–5 business days) or switch provider URLs if already certified → API key.
  - Client must have RNC with no debts and up-to-date data in DGII.
- **GAE:** API with 3 endpoints, from ~$9/month unlimited invoices (per Odoo listing).
- **Grodtech ECF:** HTTPS gateway, accepts JSON **or XML**, server-side signing.
- **Alanube:** Alegra's provider.
- Always confirm the provider is on DGII's official authorized list.

**Integration model**
- One **integrator account**; create each client's company via API; client only sees your app (no "sign in with ECF SSD").
- API key lives in **your cloud**, not on registers (billing control, suspension, offline queue).
- Manual steps that remain the client's: getting the digital certificate, DGII Oficina Virtual actions. Wizard guides them with a checklist and status checks.
- Bundle e-CF in plans with a fair-use cap + overage add-on.

**Offline / contingency (key design driver)**
- Internet down but e-CF can still be generated: issue offline, print with contingency note, **send to DGII within 72 hours**. Paper B-series is NOT allowed just for an internet outage.
- Can't generate e-CF at all: declare contingency in Oficina Virtual, use B receipts max 15 days, regularize with e-CFs within 30 days.
- Expired certificate is not a contingency.
- ⇒ **Signing must happen locally** (security code comes from the signature). Provider cloud-signing breaks offline compliance.

**Certificate (.p12) handling decision**
- Store on the **client's main PC** in the Windows cert store, **non-exportable**, usable only by the local service.
- **Do not keep central copies** (liability: a .p12 lets anyone invoice as that company). If ever needed for cloud-only clients: Azure Key Vault with non-exportable keys.
- Track renewal dates; wizard installs the renewed cert.

---

## 9. Hardware integration

**Printing**
- ESC/POS over USB/serial/network (e.g., ESCPOS_NET). 80 mm ≈ 48 chars, 58 mm ≈ 32.
- e-CF printed representation: e-NCF, security code, QR (from `ImpresionUrl`), contingency note when offline.
- Cheap clones may fail on QR/accents: fallback = render receipt as image and print bitmap.
- Drawer opens via printer command. Weighed labels: separate printer (Zebra ZPL or scale's own).
- Configurable template (logo/header/footer/width) = white-label feature.

**Scales:** serial/USB via `System.IO.Ports`; one driver per brand behind `IScale`.

**Card terminals (Azul, CardNet; devices Verifone/Ingenico/PAX)**
- Technically medium (request/response over USB/serial/network); business-wise harder: specs not public, need acquirer partnership + certification.
- **v1:** cashier types amount on terminal, selects "Card" in POS, optional auth code.
- **v2:** integrate one acquirer first (Azul likely), Premium feature.

---

## 10. Sync / replica DB

SQL Server Express can't publish replication, so sync is app-built (SQL Change Tracking + own API, or Dotmim.Sync). Avoid Azure SQL Data Sync (retiring).

| Level | What | Difficulty | Tier |
|---|---|---|---|
| Cloud backup | Encrypted backups to Azure | Easy | Pro |
| One-way replica | Store → cloud for dashboard | Medium (weeks) | Pro |
| Two-way sync | Branches share products/prices/stock | Hard | Premium |

Rule: products/prices flow **down** from cloud; sales/movements flow **up**.

## 11. AI

- **Cloud only** (local models on shop PCs too weak/slow).
- Natural-language questions on sales; reorder suggestions; anomaly alerts (voids, discounts, cash shortages); supplier invoice photo → purchase entry; daily WhatsApp summary.
- Usage-capped paid add-on.

## 12. Licensing

- Entitlements per business (active modules); one `IFeatureService`.
- **Signed license file cached locally** with 7–15 day grace period.
- Branding per reseller from config (logo, colors, name).

---

## 13. Open questions / next actions

**Ask ECF SSD**
- [ ] Plan per RNC or per account? Integrator/reseller pricing for many companies?
- [ ] Can one account manage many client companies via API (create company, **upload .p12 via API**)?
- [ ] Do they accept **pre-signed XML** (needed for local signing/offline)?
- [ ] Contingency handling, e-NCF sequence management, white-label options.
- [ ] If no pre-signed XML: compare Grodtech (accepts XML) or sending directly to DGII.

**Ask Azul / CardNet**
- [ ] Integrated-terminal SDK/protocol and certification process.

**Validate**
- [ ] Interview 5–10 butcher shops/colmados: current software, pain points, willingness to pay, hardware in use.
- [ ] Confirm cost assumptions (support, cloud, e-CF) and break-even.
- [ ] Hardware spec sheet (minimum/recommended) for clients.

**Build next**
- [ ] Solution structure (projects, layers, module/feature plug-in model).
- [ ] UI mockup of the checkout screen (product grid, scale weight, cart, payment).
- [ ] Deployment diagram (LAN, local API, cloud, mesh support).

---

## Key sources
- ECF SSD pricing: https://ecf.ssd.com.do/precios
- ECF SSD docs: https://ecf.ssd.com.do/documentacion/intro-start
- ECF SSD .NET SDK: https://www.nuget.org/packages/SSDDO.ECF_DGII.SDK
- e-CF contingency summary: https://siemprealdia.co/republica-dominicana/impuestos/contingencia-en-la-facturacion-electronica/
- DGII contingency instructions: https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Instructivos%20sobre%20Facturación%20Electrónica/Instructivo-Contingencia-FE.pdf
- e-CF deadline extension: https://blog.alegra.com/republica-dominicana/prorroga-ecf-pymes/
- Alegra POS for colmados: https://blog.alegra.com/republica-dominicana/sistema-inteligente-para-colmados/