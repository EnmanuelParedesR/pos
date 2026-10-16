import { useCallback, useEffect, useState } from "react";

/** The cloud API (src/Hosts/Toki.Host.Cloud). */
export const apiUrl = (import.meta.env.VITE_API_URL ?? "http://localhost:5100").replace(/\/$/, "");

const refreshKey = "toki.refreshToken";

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

export async function signIn(email: string, password: string) {
  const response = await send("/api/auth/login", "POST", { email, password }, false);
  if (response.status === 401) {
    // A tenant disabled by Toki staff (TK-189) answers "NotAllowed".
    const problem = await response.json().catch(() => ({}));
    if (problem.detail === "NotAllowed") throw new ApiError(401, "Auth.NotAllowed", "Esta cuenta está desactivada. Comuníquese con Toki.");
    throw new ApiError(401, "Auth.Failed", "El correo o la contraseña no coinciden.");
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
  DuplicateUserName: "Ya existe una cuenta con ese correo.",
  DuplicateEmail: "Ya existe una cuenta con ese correo.",
  InvalidEmail: "El correo no es válido.",
  InvalidUserName: "El correo no es válido.",
};

export function identityMessages(error: ApiError): string[] {
  const keys = Object.keys(error.errors);
  if (keys.length === 0) return [error.detail];
  return [...new Set(keys.map((k) => identityErrors[k] ?? "Revise los datos e intente de nuevo."))];
}

export async function register(email: string, password: string) {
  const response = await send("/api/auth/register", "POST", { email, password }, false);
  if (!response.ok) throw await toError(response);
  await signIn(email, password);
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

/** `isPlatformAdmin`: Toki staff (TK-189), who also see the "Plataforma" area. */
/** `role`: in the current tenant (TK-217); a ReadOnly member sees the panel but cannot change anything (the API refuses it anyway). */
export type MemberRole = "Owner" | "ReadOnly";
export const roleNames: Record<MemberRole, string> = { Owner: "Dueño", ReadOnly: "Solo lectura" };
export interface MyTenant { tenantId: string; name: string; role: MemberRole }
/** `canViewCosts` (TK-201): Owners always; a Solo lectura member only when an Owner allowed it. Without it profit comes back null. */
export interface Me { email: string | null; tenantId: string; tenantName: string; isPlatformAdmin?: boolean; role: MemberRole; tenants: MyTenant[]; canViewCosts?: boolean }
export interface Member { userId: string; email: string | null; role: MemberRole; joinedUtc: string; canViewCosts?: boolean }
export interface Invite { id: string; email: string; role: MemberRole; invitedBy: string; createdUtc: string; expiresUtc: string }
export interface Members { members: Member[]; invites: Invite[] }
/** `link`: null when the invitation went out by e-mail (R8 #1); the owner never sees it then. */
export interface CreatedInvite { invite: Invite; link: string | null }
export interface InvitePreview { tenantName: string; email: string; role: MemberRole; expiresUtc: string }
export interface Store { id: string; name: string; createdUtc: string; lastEventUtc: string | null; lastSyncUtc: string | null; nodes: number }
export interface ConnectionCode { code: string; expiresUtc: string }

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
export interface PublicCatalog { storeName: string; whatsApp: string | null; items: PublicCatalogItem[] }
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

// Plataforma (Toki staff, TK-189 / TK-190): /api/platform/*.

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
}
