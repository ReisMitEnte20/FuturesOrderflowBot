interface EmptyStateProps {
  title: string;
  description?: string;
  icon?: React.ReactNode;
  action?: React.ReactNode;
}

export function EmptyState({ title, description, icon, action }: EmptyStateProps) {
  return (
    <div className="flex flex-col items-center justify-center py-12 gap-3">
      {icon && <div className="text-[var(--fg-faint)]">{icon}</div>}
      <p className="text-sm text-[var(--fg-dim)]">{title}</p>
      {description && <p className="text-xs text-[var(--fg-faint)]">{description}</p>}
      {action}
    </div>
  );
}