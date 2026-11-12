import { cn } from "@/lib/utils";

interface BadgeProps {
  children: React.ReactNode;
  variant?: "default" | "success" | "danger" | "warning" | "info" | "neutral";
  dot?: boolean;
  className?: string;
}

export function Badge({ children, variant = "default", dot, className }: BadgeProps) {
  const colorMap = {
    default: "border-[var(--line-2)] bg-[var(--panel-2)] text-[var(--fg-dim)]",
    success: "border-[rgba(60,240,160,0.35)] bg-[rgba(60,240,160,0.12)] text-[var(--key)]",
    danger: "border-[rgba(240,86,106,0.35)] bg-[rgba(240,86,106,0.12)] text-[var(--red)]",
    warning: "border-[rgba(240,195,90,0.35)] bg-[rgba(240,195,90,0.12)] text-[var(--gold)]",
    info: "border-[rgba(34,184,240,0.35)] bg-[rgba(34,184,240,0.12)] text-[var(--cyan)]",
    neutral: "border-[var(--line)] bg-[var(--panel-2)] text-[var(--fg-faint)]",
  };

  return (
    <span
      className={cn(
        "inline-flex items-center gap-1 px-1.5 py-0.5 rounded-md border text-[10px] font-mono uppercase tracking-wider",
        colorMap[variant],
        className
      )}
    >
      {dot && (
        <span
          className={cn(
            "w-1 h-1 rounded-full",
            variant === "success" && "bg-[var(--key)] shadow-[0_0_6px_rgba(60,240,160,0.9)]",
            variant === "danger" && "bg-[var(--red)] shadow-[0_0_6px_rgba(240,86,106,0.8)]",
            variant === "warning" && "bg-[var(--gold)]",
            variant === "info" && "bg-[var(--cyan)]",
            (variant === "neutral" || variant === "default") && "bg-[var(--fg-faint)]"
          )}
        />
      )}
      {children}
    </span>
  );
}
