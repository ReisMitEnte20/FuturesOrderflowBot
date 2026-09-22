import { cn } from "@/lib/utils";
import type { ReactNode } from "react";

interface Column<T> {
  key: string;
  header: string;
  className?: string;
  render?: (value: unknown, index: number, row: T) => ReactNode;
}

interface TableProps<T> {
  columns: Column<T>[];
  data: T[];
  keyField: keyof T;
  className?: string;
}

export function Table<T>({ columns, data, keyField, className }: TableProps<T>) {
  if (!data.length) {
    return (
      <div className="p-8 text-center text-sm text-[var(--fg-faint)]">
        No data available
      </div>
    );
  }

  return (
    <div className={cn("overflow-x-auto", className)}>
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b border-[var(--line)]">
            {columns.map((col) => (
              <th
                key={col.key}
                className={cn(
                  "text-left px-3 py-2 text-[10px] font-mono uppercase tracking-wider text-[var(--fg-faint)]",
                  col.className
                )}
              >
                {col.header}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {data.map((row, i) => (
            <tr key={String(row[keyField])} className="border-b border-[var(--line)] hover:bg-[var(--bg-2)]">
              {columns.map((col) => {
                const value = row[col.key as keyof T];
                return (
                  <td
                    key={col.key}
                    className={cn("px-3 py-2 text-[var(--fg-dim)]", col.className)}
                  >
                    {col.render ? col.render(value, i, row) : (
                      <span className="font-mono text-xs">{String(value ?? "—")}</span>
                    )}
                  </td>
                );
              })}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}