import type { ReactNode } from 'react';

/**
 * MD3 пустое состояние (issue #110, фаза 3): dashed-контейнер, круглая плашка-иконка
 * (secondary-container), заголовок + поясняющий текст, одна filled-кнопка действия.
 * Для списков без данных (стройки, наборы, документы комплекта и т.п.).
 */
export function EmptyState({
  icon, title, description, action, className = '',
}: {
  icon: ReactNode;
  title: string;
  description?: string;
  action?: ReactNode;
  className?: string;
}) {
  return (
    <div className={`flex flex-col items-center gap-4 rounded-2xl border border-dashed border-stroke px-6 py-12 text-center ${className}`}>
      <div className="flex h-16 w-16 items-center justify-center rounded-full bg-tonal text-on-tonal">
        {icon}
      </div>
      <div className="space-y-1">
        <h3 className="text-base font-medium text-fg1">{title}</h3>
        {description && <p className="mx-auto max-w-sm text-sm text-fg3">{description}</p>}
      </div>
      {action && <div className="pt-1">{action}</div>}
    </div>
  );
}
