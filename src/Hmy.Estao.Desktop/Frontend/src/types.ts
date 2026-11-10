import type { UserMessage } from "./i18n";
export interface Pacing { enabled: boolean; dailyTargetPercent: number; notifyOnExceed: boolean }
export interface UsageAlerts {
  enabled: boolean; warningPercent: number; warningColor: string; criticalPercent: number; criticalColor: string;
}
export interface Provider {
  authentication?: { signedIn: boolean | null; message: string; messageInfo?: UserMessage; checking?: boolean } | null;
  id: string; name: string; enabled: boolean; source: string; cookieSource: string;
  workspaceId: string | null; enterpriseHost: string | null; region: string | null;
  codexProfileHomePaths: string[] | null; pacing: Pacing; hasApiKey: boolean; hasCookie: boolean;
  apiKey?: string; newCookie?: string; clearApiKey?: boolean; clearCookie?: boolean;
}
export interface RateWindow {
  titleMessage?: UserMessage;
  unitMessage?: UserMessage | null;
  id: string; title: string; percentUsed: number | null; resetAt: string | null;
  used: number | null; limit: number | null; unit: string | null;
}
export interface Snapshot {
  retryAt?: string | null;
  provider: string; displayName: string; source: string; updatedAt: string; windows: RateWindow[];
  account: string | null; plan: string | null; error: string | null; isStale: boolean;
  credits: { balance: number | null; unit: string | null; unlimited: boolean | null } | null;
  errorMessage?: UserMessage | null;
  accountMessage?: UserMessage | null;
}
export interface HistoryPoint { provider: string; window: string; timestamp: string; percentUsed: number }
export interface ChartPoint { timestamp: string; value: number }
export interface ChartSeries { window: string; label: UserMessage; points: ChartPoint[]; kind: "usage" | "target" | "projection"; colorIndex: number }
export interface UsageChart {
  provider: string; startAt: string; endAt: string; observedAt: string; nowAt: string; range: string; series: ChartSeries[];
  forecast: { label: UserMessage; at: string | null; resetOccursFirst: boolean; unavailable: boolean };
}
export interface SettingsUpdate {
  theme: string;
  language?: string;
  providers: Provider[]; usageAlerts: UsageAlerts; refresh: { enabled: boolean; intervalMinutes: number };
  overlayEnabled: boolean; overlayProviderIds: string[]; launchAtSignIn: boolean;
}
export interface DesktopState extends SettingsUpdate {
  charts?: UsageChart[] | null;
  effectiveLanguage?: string;
  providerSignIn?: { provider: string; pending: boolean; userCode: string | null; message: string; isError?: boolean; messageInfo?: UserMessage; operation?: string } | null;
  statusMessage?: UserMessage | null;
  view: string; selectedProvider: string | null; snapshots: Snapshot[]; history: HistoryPoint[];
  accounts: Record<string, { index: number; label: string; labelMessage?: UserMessage }[]>; status: string | null;
}
