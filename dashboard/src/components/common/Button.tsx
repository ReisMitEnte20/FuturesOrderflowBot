import { cn } from "@/lib/utils";

interface ButtonProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: "default" | "primary" | "danger";
  size?: "sm" | "md";
}

export function Button({ variant = "default", size = "md", className, children, ...props }: ButtonProps) {
  return (
    <button
      className={cn(
        "rounded-md font-mono text-xs transition-colors focus-visible:outline focus-visible:outline-2 focus-visible:outline-[var(--key)]",
        variant === "primary" && "bg-[var(--key)] text-[var(--bg)] hover:opacity-90 px-3 py-2",
        variant === "danger" && "bg-[var(--red)]/20 text-[var(--red)] hover:bg-[var(--red)]/30 px-3 py-2",
        variant === "default" && "bg-[var(--panel-2)] text-[var(--fg-dim)] hover:bg-[var(--panel-3)] px-3 py-2",
        size === "sm" && "text-xs px-2 py-1",
        size === "md" && "text-sm px-4 py-2",
        className
      )}
      {...props}
    >
      {children}
    </button>
  );
}