import { useCallback, useEffect, useState } from "react";

/** The cloud API (src/Hosts/Abastalia.Host.Cloud). */
export const apiUrl = (import.meta.env.VITE_API_URL ?? "http://localhost:5100").replace(/\/$/, "");

const refreshKey = "abastalia.refreshToken";

/** A failed call: `code` is the API's error code (ProblemDetails), `detail` the Spanish text to show. */
export class ApiError extends Error {
  constructor(
    public status: number,
    public code: string,
    public detail: string,
    public errors: Record<string, string[]> = {},
  ) {
    super(detail);
  }

  /** No answer from the server: shown in amber, like the register when it is offline. */
  get offline() {
    return this.status === 0;
  }
}

// The access token lives only in memory; the refresh token survives a reload.
let accessToken: string | null = null;
let refreshing: Promise<boolean> | null = null;
let onSignedOut = () => {};

function readRefreshToken() {
  try {
    return localStorage.getItem(refreshKey);
  } catch {
    return null;
  }
}

function saveTokens(tokens: { accessToken: string; refreshToken: string } | null) {
  accessToken = tokens?.accessToken ?? null;
  try {
    if (tokens) localStorage.setItem(refreshKey, tokens.refreshToken);
    else localStorage.removeItem(refreshKey);
  } catch {
    // Private mode or blocked storage: the session lasts until the tab closes.
  }
}

export const hasSession = () => accessToken !== null || readRefreshToken() !== null;

/** Called when the session cannot be renewed (the app shows the sign-in screen). */
export function setOnSignedOut(handler: () => void) {
  onSignedOut = handler;
}

export function signOut() {
  saveTokens(null);
  onSignedOut();
}

async function send(path: string, method: string, body: unknown, auth: boolean) {
  const headers: Record<string, string> = { Accept: "application/json" };
  if (body !== undefined) headers["Content-Type"] = "application/json";
  if (auth && accessToken) headers.Authorization = `Bearer ${accessToken}`;
  try {
    return await fetch(apiUrl + path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  } catch {
    throw new ApiError(0, "Network", "No hay conexión con el servidor. Revise el internet e intente de nuevo.");
  }
}

async function toError(response: Response): Promise<ApiError> {
  const problem = await response.json().catch(() => ({}));
  const detail =
    problem.detail ??
    (response.status === 401
      ? "Su sesión terminó. Vuelva a entrar."
      : response.status === 403
        ? "Su cuenta no tiene permiso para esto. Pídaselo al dueño del negocio."
        : response.status >= 500
        ? "El servidor tuvo un problema. Intente de nuevo en un momento."
        : "No se pudo completar la operación.");
  return new ApiError(response.status, problem.code ?? problem.title ?? String(response.status), detail, problem.errors ?? {});
}

/** One refresh at a time: parallel 401s wait for the same renewal. */
function refresh(): Promise<boolean> {
  refreshing ??= (async () => {
    const refreshToken = readRefreshToken();
    if (!refreshToken) return false;
    const response = await send("/api/auth/refresh", "POST", { refreshToken }, false);
    if (!response.ok) return false;
    saveTokens(await response.json());
    return true;
  })().finally(() => (refreshing = null));
  return refreshing;
}

/** Calls the API with the session; on a 401 it renews the token once and retries. */
export async function api<T>(path: string, method = "GET", body?: unknown): Promise<T> {
  let response = await send(path, method, body, true);
  if (response.status === 401) {
    // A renewal that cannot reach the server throws (offline) and keeps the session for later.
    if (!(await refresh())) {
      saveTokens(null);
      onSignedOut();
      throw await toError(response);
    }
    response = await send(path, method, body, true);
  }
  if (!response.ok) throw await toError(response);
  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

/** TK-238: saves a file the API streams with the session (a store backup), as the browser's own download. */
export async function download(path: string, fileName: string) {
  let response = await send(path, "GET", undefined, true);
  if (response.status === 401 && (await refresh())) response = await send(path, "GET", undefined, true);
  if (!response.ok) throw await toError(response);
  const url = URL.createObjectURL(await response.blob());
  const link = document.createElement("a");
  link.href = url;
  link.download = fileName;
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/** `twoFactorCode`: the authenticator app's code, for an account with 2FA on (R11 B-Med6); without it such an account gets "Auth.RequiresTwoFactor".
 * TK-298: a backup code (lost phone) goes as `twoFactorRecoveryCode` instead; each works once. */
export async function signIn(email: string, password: string, twoFactorCode?: string, recoveryCode?: string) {
  const second = recoveryCode ? { twoFactorRecoveryCode: recoveryCode } : { twoFactorCode: twoFactorCode || undefined };
  const response = await send("/api/auth/login", "POST", { email, password, ...second }, false);
  if (response.status === 401) {
    const problem = await response.json().catch(() => ({}));
    // R11 B-High1: an unconfirmed e-mail answers "NotAllowed", as a business disabled by Abastalia staff (TK-189) does.
    if (problem.detail === "NotAllowed")
      throw new ApiError(401, "Auth.NotAllowed", "No puede entrar todavía: confirme su correo con el enlace que le enviamos. Si ya lo confirmó, comuníquese con Abastalia.");
    if (problem.detail === "RequiresTwoFactor") throw new ApiError(401, "Auth.RequiresTwoFactor", "Escriba el código de 6 dígitos de su aplicación de autenticación.");
    if (problem.detail === "LockedOut") throw new ApiError(401, "Auth.LockedOut", "Demasiados intentos. Espere 15 minutos e intente de nuevo.");
    throw new ApiError(
      401,
      "Auth.Failed",
      recoveryCode
        ? "Ese código de respaldo no es válido o ya se usó."
        : twoFactorCode
          ? "El código no es válido. Revise la hora del teléfono e intente de nuevo."
          : "El correo o la contraseña no coinciden.",
    );
  }
  if (!response.ok) throw await toError(response);
  saveTokens(await response.json());
}

/** Identity's error keys, in the owner's words. */
const identityErrors: Record<string, string> = {
  PasswordTooShort: "La contraseña debe tener al menos 8 caracteres.",
  PasswordRequiresDigit: "La contraseña debe tener al menos un número.",
  PasswordRequiresLower: "La contraseña debe tener al menos una letra minúscula.",
  PasswordRequiresUpper: "La contraseña debe tener al menos una letra mayúscula.",
  PasswordRequiresNonAlphanumeric: "La contraseña debe tener al menos un símbolo.",
  PasswordRequiresUniqueChars: "La contraseña necesita más caracteres distintos.",
  DuplicateUserName: "Ese correo ya tiene una cuenta.",
  DuplicateEmail: "Ese correo ya tiene una cuenta.",
  InvalidToken: "El enlace no es válido o ya venció. Pida uno nuevo.",
  InvalidEmail: "El correo no es válido.",
  InvalidUserName: "El correo no es válido.",
};

export function identityMessages(error: ApiError): string[] {
  const keys = Object.keys(error.errors);
  if (keys.length === 0) return [error.detail];
  return [...new Set(keys.map((k) => identityErrors[k] ?? "Revise los datos e intente de nuevo."))];
}

/** R11 B-High1: the account signs in once its e-mail is confirmed (the link of the e-mail sent now). R11 B-Low5: the answer is the same whether
 * or not the e-mail already had an account. */
export async function register(email: string, password: string) {
  const response = await send("/api/auth/register", "POST", { email, password }, false);
  if (!response.ok) throw await toError(response);
}

/** TK-230: asks for Identity's reset e-mail. Always 200, whether the account exists or not (no account enumeration). */
export async function forgotPassword(email: string) {
  const response = await send("/api/auth/forgotPassword", "POST", { email }, false);
  if (!response.ok) throw await toError(response);
}

/** TK-230: the code from the e-mailed link (/app/restablecer#email=…&code=…) and the new password. */
export async function resetPassword(email: string, resetCode: string, newPassword: string) {
  const response = await send("/api/auth/resetPassword", "POST", { email, resetCode, newPassword }, false);
  if (!response.ok) throw await toError(response);
}

/** TK-230: the link of the confirmation e-mail sent on sign-up (/app/confirmar#…); only a confirmed address signs in and gets a reset e-mail.
 * R11 B-Low1: the code goes in the body, never in a URL that telemetry keeps. `firstConfirmation`: the account had never confirmed (the page
 * warns to change the password if the person did not create it). The link works once: the same promise for the same link (React's double effect).
 * TK-297: `alreadyConfirmed` = the link was used before (another tab, the mail app); `email` comes only with a valid code, to fill in Entrar. */
export interface ConfirmedEmail { firstConfirmation: boolean; alreadyConfirmed?: boolean; email?: string | null }
const confirmations = new Map<string, Promise<ConfirmedEmail>>();
export function confirmEmail(userId: string, code: string, changedEmail?: string) {
  const key = `${userId}|${code}|${changedEmail ?? ""}`;
  let pending = confirmations.get(key);
  if (!pending) {
    pending = send("/api/auth/confirm", "POST", { userId, code, changedEmail }, false).then(async (response) => {
      if (!response.ok) throw new ApiError(response.status, "Auth.ConfirmFailed", "El enlace no es válido o venció.");
      return (await response.json()) as ConfirmedEmail;
    });
    confirmations.set(key, pending);
  }
  return pending;
}

/** R11 B-Med6: the account's two-step verification (Identity's /manage/2fa). `sharedKey` goes in the authenticator app. */
export interface TwoFactorStatus { sharedKey: string; recoveryCodesLeft: number; recoveryCodes?: string[] | null; isTwoFactorEnabled: boolean; isMachineRemembered: boolean }
export const twoFactor = (body: { enable?: boolean; twoFactorCode?: string; resetSharedKey?: boolean; resetRecoveryCodes?: boolean } = {}) =>
  api<TwoFactorStatus>("/api/auth/manage/2fa", "POST", body);

/** TK-231: a new confirmation e-mail. Always 200 whether or not the account exists or needs it (no enumeration); 429 when asked too often. */
export async function resendConfirmation(email: string) {
  const response = await send("/api/auth/resendConfirmationEmail", "POST", { email }, false);
  if (!response.ok) throw await toError(response);
}

/** TK-217: a new account for the invited e-mail, already a member of the inviting business (no business of its own). */
export async function registerByInvite(token: string, email: string, password: string) {
  // R8 #12: the token goes in the body, never in a URL.
  const response = await send("/api/invites/register", "POST", { token, password }, false);
  if (!response.ok) throw await toError(response);
  await signIn(email, password);
}

/** A new access token now (after joining or switching business: the token carries the business). */
export async function renewSession() {
  if (!(await refresh())) throw new ApiError(401, "Auth.Expired", "Su sesión terminó. Vuelva a entrar.");
}

/** Loads `path` (null = wait) and reloads it when the path changes or `reload()` is called. */
export function useApi<T>(path: string | null) {
  const [state, setState] = useState<{ data?: T; error?: ApiError; loading: boolean }>({ loading: path !== null });
  const [version, setVersion] = useState(0);
  useEffect(() => {
    if (path === null) return;
    let live = true;
    setState((s) => ({ ...s, loading: true }));
    api<T>(path).then(
      (data) => live && setState({ data, loading: false }),
      (error) => live && setState({ error: error instanceof ApiError ? error : new ApiError(0, "Unknown", String(error)), loading: false }),
    );
    return () => {
      live = false;
    };
  }, [path, version]);
  return { ...state, reload: useCallback(() => setVersion((v) => v + 1), []) };
}

// Shapes returned by the cloud API (camelCase).

/** `isPlatformAdmin`: Abastalia staff (TK-189), who also see the "Plataforma" area. */
/** `role`: in the current tenant (TK-217); a ReadOnly member sees the panel but cannot change anything (the API refuses it anyway). */
export type MemberRole = "Owner" | "ReadOnly";
export const roleNames: Record<MemberRole, string> = { Owner: "Dueño", ReadOnly: "Solo lectura" };
export interface MyTenant { tenantId: string; name: string; role: MemberRole }
/** `canViewCosts` (TK-201): Owners always; a Solo lectura member only when an Owner allowed it. Without it profit comes back null. */
/** `emailConfirmed` (TK-231): false = no reset e-mail reaches it; the panel offers to resend the confirmation. */
export interface Me { email: string | null; tenantId: string; tenantName: string; isPlatformAdmin?: boolean; role: MemberRole; tenants: MyTenant[]; canViewCosts?: boolean; emailConfirmed?: boolean }
export interface Member { userId: string; email: string | null; role: MemberRole; joinedUtc: string; canViewCosts?: boolean }
export interface Invite { id: string; email: string; role: MemberRole; invitedBy: string; createdUtc: string; expiresUtc: string }
export interface Members { members: Member[]; invites: Invite[] }
/** `link`: null when the invitation went out by e-mail (R8 #1); the owner never sees it then. */
export interface CreatedInvite { invite: Invite; link: string | null }
export interface InvitePreview { tenantName: string; email: string; role: MemberRole; expiresUtc: string }
export interface Store { id: string; name: string; createdUtc: string; lastEventUtc: string | null; lastSyncUtc: string | null; nodes: number }
export interface ConnectionCode { code: string; expiresUtc: string }
/** TK-238: a store backup kept in the cloud, encrypted with the owner's passphrase (the cloud cannot open it). `createdUtc`: when the store made it. */
export interface StoreBackup { id: string; fileName: string; bytes: number; createdUtc: string; uploadedUtc: string }

export type PaymentMethod = "Cash" | "Card" | "Transfer" | "OnAccount" | "CreditBalance";
export interface PaymentMix { method: PaymentMethod; count: number; amount: number }
export interface Issue {
  kind: "ShiftDifference" | "SyncStale" | "EventsFailed";
  storeId: string;
  storeName: string;
  referenceId: string | null;
  amount: number | null;
  occurredUtc: string | null;
  message: string;
  /** Null when the issue is not about a shift or the store has not sent the name yet. */
  cashierName?: string | null;
  registerName?: string | null;
}
export interface Summary {
  date: string;
  salesTotal: number;
  salesCount: number;
  averageTicket: number;
  discountTotal: number;
  itbisTotal: number;
  returnsTotal: number;
  netTotal: number;
  voidedCount: number;
  byHour: { hour: number; count: number; total: number }[];
  paymentMix: PaymentMix[];
  issues: Issue[];
  lastEventUtc: string | null;
}
export interface DaySales { date: string; count: number; total: number; discount: number; itbis: number; returns: number; net: number; voidedCount: number }
/** `unit`: "Pound" or "Unit"; null for sales the store uploaded before TK-180. */
/** `profit`: null for a member who may not see costs (TK-201). */
export interface ProductSales { productId: string; description: string; quantity: number; total: number; profit: number | null; unit?: string | null }
export interface CashierSales { cashierId: string; count: number; total: number; voidedCount: number; cashierName?: string | null }
/** `customerName`: null until the store sends the customer's account (TK-174). */
export interface Receivables { total: number; customerCount: number; topDebtors: { customerId: string; storeId: string; debt: number; customerName?: string | null }[] }
export interface Shift {
  shiftId: string;
  storeId: string;
  storeName: string;
  registerId: string;
  cashierId: string;
  cashierName?: string | null;
  registerName?: string | null;
  zNumber: number;
  expectedCash: number;
  countedCash: number;
  difference: number;
  closedUtc: string;
}
/** TK-200 in the panel: one closed shift with what the cloud has of its Z. */
export interface ShiftReport {
  shift: Shift;
  openingFloat: number | null;
  openedUtc: string | null;
  salesCount: number;
  salesTotal: number;
  voidedCount: number;
  payments: PaymentMix[];
  cashMovements: { type: string; method: PaymentMethod; count: number; amount: number }[];
}
/** TK-207: an archived e-CF. `ready` is false while a document in deep archive comes back (hours). */
export interface ArchivedEcf {
  eNcf: string;
  status: string;
  environment: string;
  signedUtc: string | null;
  receivedUtc: string;
  replacedByENcf: string;
  cold: boolean;
  ready: boolean;
  signedXml: string | null;
  rfceXml: string | null;
}
/** TK-195: a store's public catalog (no sign-in). Never cost or stock. */
export interface PublicCatalogItem { productId: string; name: string; category: string; unit: "Unit" | "Pound"; price: number }
/** TK-280: logoVersion is null when the store has no logo; else `/api/public/catalog/{storeId}/logo?v=` serves it. */
export interface PublicCatalog { storeName: string; whatsApp: string | null; items: PublicCatalogItem[]; logoVersion?: string | null }
export interface NodeStatus { nodeId: string; name: string; lastEventUtc: string | null; lastSyncUtc: string | null }
export interface StoreSyncStatus {
  storeId: string;
  storeName: string;
  lastEventUtc: string | null;
  lastSyncUtc: string | null;
  pendingEvents: number;
  failedEvents: number;
  nodes: NodeStatus[];
}

// Plataforma (Abastalia staff, TK-189 / TK-190): /api/platform/*.

/** TK-254: a version released on a channel to a share of the stores (Plataforma → Versiones). */
export type ReleaseChannel = "pilot" | "stable";
export interface AppRelease {
  id: string;
  channel: ReleaseChannel;
  version: string;
  rolloutPercent: number;
  notesUrl: string | null;
  publishedUtc: string;
  changedUtc: string;
  changedBy: string;
}
/** Store PCs (and stores) whose last heartbeat reported each version. */
export interface FieldVersion {
  version: string;
  nodes: number;
  stores: number;
}
export interface ReleasesResponse {
  releases: AppRelease[];
  field: FieldVersion[];
}

export type FunnelStage = "account" | "store" | "connected" | "firstSale" | "sevenDays" | "certified";
/** TK-225: a store in the DGII certification. `currentStep` is 0-based (`totalSteps` when every step is ticked); `stuck`: 14 days or more on one step. */
export interface CertificationProgress {
  tenantId: string;
  tenantName: string;
  storeId: string;
  storeName: string;
  currentStep: number;
  totalSteps: number;
  stepsDone: number;
  certified: boolean;
  stepSinceUtc: string;
  daysInStep: number;
  stuck: boolean;
}
/** TK-234: an accompaniment request in the e-CF certification (TK-233). Dates are UTC; `notes` are staff only. */
export type AccompanimentStatus = "New" | "Scheduled" | "Session1Done" | "Session2Done" | "Certified" | "Cancelled";
export interface PlatformAccompaniment {
  id: string;
  tenantId: string;
  tenantName: string;
  storeId: string;
  storeName: string;
  contactName: string;
  phone: string;
  preferredTimes: string;
  status: AccompanimentStatus;
  session1Utc: string | null;
  session2Utc: string | null;
  notes: string | null;
  createdUtc: string;
  changedUtc: string;
}
/** R11 B-High2: a receiver activation with a person's certificate (cédula) for a company's RNC, waiting for support. `approved` null = pending. */
export interface FeDelegation {
  id: string;
  tenantId: string;
  tenantName: string;
  rnc: string;
  subject: string;
  holderName: string;
  requestedUtc: string;
  approved: boolean | null;
  decidedBy: string | null;
  decidedUtc: string | null;
}
export interface PlatformTenant {
  id: string;
  name: string;
  ownerEmail: string | null;
  createdUtc: string;
  disabledUtc: string | null;
  stores: number;
  nodes: number;
  lastActivityUtc: string | null;
  stage: FunnelStage;
  appVersion: string | null;
  /** TK-196: from the newest heartbeat that carries it (null on older versions). */
  licenseStatus?: string | null;
  licenseEndsUtc?: string | null;
}
export interface PlatformNode {
  id: string;
  storeId: string;
  name: string;
  createdUtc: string;
  revokedUtc: string | null;
  lastSyncUtc: string | null;
  lastEventUtc: string | null;
  pendingEvents: number;
  failedEvents: number;
  heartbeatUtc: string | null;
  appVersion: string | null;
  os: string | null;
  uptimeMinutes: number | null;
  outboxBacklog: number | null;
  lastErrorCode: string | null;
}
export interface PlatformStore {
  id: string;
  name: string;
  createdUtc: string;
  registers: number;
  users: number;
  firstSaleDay: string | null;
  lastSaleDay: string | null;
  nodes: PlatformNode[];
}
export interface TenantMetrics {
  tenantId: string;
  stage: FunnelStage;
  daysWithSalesLast30: number;
  salesLast30Days: number;
  amountLast30Days: number;
  firstSaleDay: string | null;
  lastSaleDay: string | null;
}
/** TK-248: this store month's use of each metered service against the plan's limit (docs/cuotas.md). */
export type Meter = "AssistantQuestions" | "AssistantTokens" | "SupplierInvoices" | "Emails" | "ArchiveRetrievals" | "AccompanimentRequests" | "Invites" | "CloudBackups" | "ReceiverSignatures";
export interface Usage { plan: string; addOns: string[]; resetsOn: string; meters: { meter: Meter; used: number; limit: number }[] }
export interface PlatformUsage { usage: Usage; plans: string[]; addOns: string[] }
/** TK-249: Paddle Billing. `enabled` false = not set up yet (the page shows the plan and how to ask Abastalia). */
export type SubscriptionStatus = "None" | "Trialing" | "Active" | "PastDue" | "Paused" | "Canceled";
export type BillingInterval = "Monthly" | "Annual";
/** A Paddle price: `amount` in the currency's smallest unit, as Paddle sends it ("1000" = US$10.00). */
export interface CatalogPrice { priceId: string; interval: BillingInterval | null; amount: string; currency: string }
/** A plan or add-on with its Paddle prices (null = not sold that way); `name` is the Paddle product's. */
export interface BillingOption { code: string; name: string; monthly: CatalogPrice | null; annual: CatalogPrice | null }
/** GET /api/public/prices: what /precios shows; `enabled` false = "Precio por confirmar". */
export interface PublicPrices { enabled: boolean; plans: BillingOption[]; addOns: BillingOption[] }

/** "US$10.00 al mes": the price as Paddle has it (two decimals; Paddle sends the smallest unit). */
export function priceLabel(price: CatalogPrice) {
  const value = new Intl.NumberFormat("en-US", { style: "currency", currency: price.currency, currencyDisplay: "symbol" }).format(Number(price.amount) / 100);
  return `${price.currency === "USD" ? "US" : ""}${value}${price.interval === "Annual" ? " al año" : price.interval === "Monthly" ? " al mes" : ""}`;
}
export interface Billing {
  enabled: boolean;
  environment: "sandbox" | "live" | null;
  plan: string;
  addOns: string[];
  status: SubscriptionStatus;
  interval: BillingInterval | null;
  paidThroughUtc: string | null;
  nextBilledUtc: string | null;
  pastDueSinceUtc: string | null;
  manualUntilUtc: string | null;
  canManage: boolean;
  plans: BillingOption[];
  addOnOptions: BillingOption[];
}
/** What Paddle.js needs to open the checkout. A business already subscribed gets `{ changed: true }` instead (Paddle changed its items). */
export interface Checkout {
  environment: "sandbox" | "live";
  clientToken: string;
  items: { priceId: string; quantity: number }[];
  customerEmail: string | null;
  customData: Record<string, string>;
}
export interface PlatformBilling {
  subscription: {
    status: SubscriptionStatus;
    interval: BillingInterval | null;
    paddleSubscriptionId: string | null;
    paddleCustomerId: string | null;
    paidThroughUtc: string | null;
    nextBilledUtc: string | null;
    pastDueSinceUtc: string | null;
    manualUntilUtc: string | null;
    manualNote: string | null;
    /** TK-279: blocked from the console (its stores stop selling once they pull the license); the reason of the last block or unblock. */
    blockedUtc?: string | null;
    blockNote?: string | null;
    updatedUtc: string | null;
  };
  events: { id: string; type: string; occurredUtc: string; receivedUtc: string }[];
}
/** TK-255: "Recomiende Abastalia". `amount` as Paddle takes it: percent, or the currency's smallest unit for "flat". */
export interface ReferralReward { type: "percentage" | "flat"; amount: string; currency: string }
/** `nextDiscount.fromUtc` = the payment it lowers; null = queued (next cycle or once the payment is up to date). */
export interface Referrals {
  enabled: boolean;
  code: string | null;
  pending: number;
  applied: number;
  reward: ReferralReward | null;
  /** TK-276: `count` > 1 = an annual plan's rewards joined on the renewal (the amount is one reward's). */
  nextDiscount: (ReferralReward & { fromUtc: string | null; count?: number }) | null;
}
export type ReferralStatus = "Pending" | "Applied" | "Reverted" | "Rejected";
export interface ReferralRow {
  id: string;
  referrerTenantId: string;
  referrerName: string;
  referredTenantId: string;
  referredName: string;
  status: ReferralStatus;
  reason: string | null;
  discountType: string;
  discountAmount: string;
  currency: string;
  createdUtc: string;
  appliedUtc: string | null;
  discountFromUtc: string | null;
  revertedUtc: string | null;
  note: string | null;
}
export interface PlatformReferrals { code: string | null; referredByCode: string | null; referrals: ReferralRow[] }
export interface PlatformTenantDetail {
  id: string;
  name: string;
  createdUtc: string;
  disabledUtc: string | null;
  members: { email: string | null; role: MemberRole; joinedUtc: string }[];
  stores: PlatformStore[];
  connectionCodes: { storeId: string; issuedUtc: string; expiresUtc: string; usedUtc: string | null }[];
  issues: Issue[];
  metrics: TenantMetrics;
}
export interface PlatformStoreDetail {
  id: string;
  tenantId: string;
  tenantName: string;
  name: string;
  createdUtc: string;
  checklist: {
    connected: boolean;
    heartbeatReceived: boolean;
    businessNameSet: boolean | null;
    rncSet: boolean | null;
    sequencesLoaded: boolean;
    activeProducts: number | null;
    firstSaleDay: string | null;
    fiscalProvider: string | null;
  };
  features: {
    onAccount: boolean;
    scaleConfigured: boolean | null;
    printerConfigured: boolean | null;
    fiscalProvider: string;
    activeRegisters: number;
    openShifts: number;
    licenseStatus?: string | null;
    licenseEndsUtc?: string | null;
  } | null;
  nodes: PlatformNode[];
  days: { day: string; sales: number; amount: number; reportedSales: number | null; activeCashiers: number | null; voids: number | null }[];
  certification: CertificationProgress | null;
}
export interface MetricsOverview {
  today: string;
  tenants: number;
  stores: number;
  nodes: number;
  nodesOnline: number;
  activeToday: number;
  activeLast7Days: number;
  activeLast30Days: number;
  salesLast30Days: number;
  amountLast30Days: number;
  funnel: { stage: FunnelStage; tenants: number }[];
  features: { feature: string; stores: number; reporting: number }[];
  health: { nodes: number; staleNodes: number; nodesWithErrors: number; nodesWithBacklog: number; failedEvents: number; versions: { version: string; nodes: number }[] };
  churn: { tenantId: string; tenantName: string; storeId: string; storeName: string; kind: "NoSales" | "Disconnected"; lastSaleDay: string | null; lastSyncUtc: string | null }[];
  certification: CertificationProgress[];
}
export interface DailyActivity { day: string; activeStores: number; sales: number; amount: number }
export interface RetentionCohort { week: string; tenants: number; activeByWeek: number[] }

/** TK-222: the member's own e-mail alerts. `emailDelivers` false: the cloud has no e-mail provider yet, nothing goes out. */
export interface AlertPreferences {
  email: string | null;
  dgiiRejected: boolean;
  certificateExpiring: boolean;
  queueStale: boolean;
  certificationProgress: boolean;
  unsubscribed: boolean;
  emailDelivers: boolean;
}
export type AlertChoice = Pick<AlertPreferences, "dgiiRejected" | "certificateExpiring" | "queueStale" | "certificationProgress">;

/** TK-154: an e-CF a supplier sent to the business. `approval`: the commercial approval, null while none. */
export interface ReceivedEcf {
  id: number;
  environment: string;
  issuerRnc: string;
  issuerName: string;
  eNcf: string;
  type: number;
  issuedOn: string;
  total: number;
  itbis: number;
  receivedUtc: string;
  approval: "Approved" | "Rejected" | null;
  approvalReason: string | null;
  approvalUtc: string | null;
  approvalDeliveredUtc: string | null;
  approvalDgiiUtc: string | null;
  approvalMessage: string | null;
  /** TK-246: null while its acknowledgement waits for the store (it arrived with the store's PC off). */
  acknowledgedUtc?: string | null;
  /** TK-250: an owner's decision from the panel; pending at the store while `approval` is null (the store's own decision, if first, wins). */
  panelDecision?: "Approved" | "Rejected" | null;
  panelReason?: string | null;
  panelDecidedBy?: string | null;
  panelDecidedUtc?: string | null;
  /** R11 B-High2: signed by a person (cédula) for the supplier's company, "firmante sin verificar": not sent to the store and not decidable. */
  signerUnverified?: boolean;
}

/** TK-241: the cloud receiver (TK-151) as the panel shows it: GET /api/panel/fe. */
export interface ReceiverUrls { environment: string; reception: string; approval: string; authentication: string }
export interface ReceiverStatus {
  active: boolean;
  rnc: string | null;
  subject: string | null;
  notAfterUtc: string | null;
  consentedUtc: string | null;
  ownerName: string | null;
  holderName: string | null;
  urls: ReceiverUrls[] | null;
  /** TK-246: StoreSigned (the store's PC signs) or CloudCustody (a copy of the certificate in Key Vault). */
  signingMode?: "StoreSigned" | "CloudCustody" | null;
}
export interface ReceiverPanel {
  status: ReceiverStatus;
  signatures: { atUtc: string; root: string; eNcf: string | null; sha256: string }[];
  approvals: { eNcf: string; buyerRnc: string; approved: boolean; reason: string | null; environment: string; receivedUtc: string }[];
  /** TK-246: hours each store PC left the receiver without its signature (last 30 days). */
  offline?: { node: string; hours: number; offlineSinceUtc: string | null }[];
}
