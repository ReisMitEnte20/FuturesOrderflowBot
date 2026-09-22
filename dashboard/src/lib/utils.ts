import { twMerge } from "tailwind-merge";
import { type ClassValue, clsx } from "clsx";

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}

export function formatNumber(num: number, decimals = 2): string {
  if (num >= 1e9) return `${(num / 1e9).toFixed(decimals)}B`;
  if (num >= 1e6) return `${(num / 1e6).toFixed(decimals)}M`;
  if (num >= 1e3) return `${(num / 1e3).toFixed(decimals)}K`;
  return num.toFixed(decimals);
}

export function formatCurrency(num: number, _currency = "USD", decimals = 2): string {
  return new Intl.NumberFormat("en-US", {
    style: "currency",
    currency: "USD",
    minimumFractionDigits: decimals,
    maximumFractionDigits: decimals,
  }).format(num);
}

export function formatPercent(num: number, decimals = 2): string {
  return `${num >= 0 ? "+" : ""}${num.toFixed(decimals)}%`;
}

export function formatPnL(pnl: number): string {
  const prefix = pnl >= 0 ? "+" : "";
  return `${prefix}${formatCurrency(pnl)}`;
}

export function getPnLColor(pnl: number): string {
  if (pnl > 0) return "text-[#a2e65d]";
  if (pnl < 0) return "text-[#c1503f]";
  return "text-[#8b857a]";
}

export function formatTimestamp(ts: string): string {
  const d = new Date(ts);
  return d.toLocaleTimeString("en-US", {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  });
}

export function formatDate(ts: string): string {
  const d = new Date(ts);
  return d.toLocaleDateString("en-US", {
    month: "short",
    day: "numeric",
  });
}