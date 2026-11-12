import { cn } from "@/lib/utils";

interface ButtonProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: "default" | "primary" | "danger";
  size?: "sm" | "md";
}

export function Button({ variant = "default", size = "md", className, children, ...props }: ButtonProps) {
  return (
    <button
      className={cn(
        "inline-flex items-center justify-center gap-1.5 rounded-lg font-mono border transition-[filter,background-color,border-color,color,box-shadow] duration-150",
        "focus-visible:outline focus-visible:outline-2 focus-visible:outline-[var(--key)]",
        "disabled:opacity-45 disabled:cursor-not-allowed disabled:shadow-none",
        // Primär: Marken-Verlauf Grün → Cyan mit Leuchten, dunkle Schrift.
        variant === "primary" &&
          "border-transparent bg-grad-brand text-[var(--bg)] font-bold shadow-brand hover:brightness-110 active:brightness-95",
        variant === "danger" &&
          "border-[rgba(240,86,106,0.35)] bg-[rgba(240,86,106,0.12)] text-[var(--red)] hover:bg-[rgba(240,86,106,0.2)]",
        variant === "default" &&
          "border-[var(--line-2)] bg-[rgba(14,33,57,0.85)] text-[var(--fg-dim)] hover:text-[var(--fg)] hover:border-[rgba(34,184,240,0.55)]",
        size === "sm" && "text-xs px-2.5 py-1",
        size === "md" && "text-sm px-4 py-2",
        className
      )}
      {...props}
    >
      {children}
    </button>
  );
}
