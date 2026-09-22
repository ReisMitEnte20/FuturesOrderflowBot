import { cn } from "@/lib/utils";

interface BadgeProps {
  children: React.ReactNode;
  variant?: "default" | "success" | "danger" | "warning" | "info" | "neutral";
  dot?: boolean;
  className?: string;
}

export function Badge({ children, variant = "default", dot, className }: BadgeProps) {
  const colorMap = {
    default: "bg-[var(--panel-2)] text-[var(--fg-dim)]",
    success: "bg-[var(--key)]/15 text-[var(--key)]",
    danger: "bg-[var(--red)]/15 text-[var(--red)]",
    warning: "bg-[var(--gold)]/15 text-[var(--gold)]",
    info: "bg-[var(--cyan)]/15 text-[var(--cyan)]",
    neutral: "bg-[var(--panel-2)] text-[var(--fg-faint)]",
  };

  return (
    <span
      className={cn(
        "inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] font-mono uppercase tracking-wider",
        colorMap[variant],
        className
      )}
    >
      {dot && (
        <span
          className={cn(
            "w-1 h-1 rounded-full",
            variant === "success" && "bg-[var(--key)]",
            variant === "danger" && "bg-[var(--red)]",
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