interface EmptyStateProps {
  title: string;
  description?: string;
  icon?: React.ReactNode;
  action?: React.ReactNode;
}

export function EmptyState({ title, description, icon, action }: EmptyStateProps) {
  return (
    <div className="flex flex-col items-center justify-center py-12 gap-2 text-center">
      {icon && (
        <div className="mb-2 grid h-14 w-14 place-items-center rounded-2xl border border-[rgba(60,240,160,0.25)] bg-[linear-gradient(180deg,rgba(60,240,160,0.10),rgba(34,184,240,0.05))] text-[var(--key)] shadow-[0_0_28px_-10px_rgba(60,240,160,0.8)]">
          {icon}
        </div>
      )}
      <p className="display text-[15px] text-[var(--fg)]">{title}</p>
      {description && <p className="text-xs text-[var(--fg-faint)] max-w-sm">{description}</p>}
      {action && <div className="mt-2">{action}</div>}
    </div>
  );
}
