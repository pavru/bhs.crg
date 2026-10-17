import type { LucideIcon } from 'lucide-react';
import type { ReactNode } from 'react';
import { Tooltip } from './Tooltip';

/**
 * Пометка состояния записи (issue #1351): черновик, разобран, оплачен, отклонён, в архиве.
 * Пилюля `text-caption`; тон — роль из токенов, а не цвет: смысл тона одинаков на всех экранах.
 *
 * - `neutral` — обычное рабочее состояние без оценки (черновик, заперт);
 * - `brand`   — в работе, передано дальше;
 * - `success` — доведено (оплачен, проведена, сформирован);
 * - `warning` — требует внимания, но не ошибка (идёт, не найдено);
 * - `danger`  — ошибка или потеря (не сформирован, запись удалена);
 * - `quiet`   — закрыто без последствий (отклонено, в архиве): контур без заливки.
 *
 * `bordered` добавляет контур в тон — для пометок, стоящих рядом с такими же на цветном фоне
 * (отчёт проверки связок). Значок — по смыслу, не для красоты: у отчёта сверки он различает
 * состояния без цвета.
 */
export type StatusTone = 'neutral' | 'brand' | 'success' | 'warning' | 'danger' | 'quiet';

const TONE: Record<StatusTone, { fill: string; border: string }> = {
  neutral: { fill: 'bg-muted text-fg2', border: 'border-stroke' },
  brand: { fill: 'bg-brand-subtle text-on-brand-subtle', border: 'border-brand/30' },
  success: { fill: 'bg-success-subtle text-success', border: 'border-success/30' },
  warning: { fill: 'bg-warning-subtle text-warning', border: 'border-warning-border' },
  danger: { fill: 'bg-danger-subtle text-danger', border: 'border-danger/30' },
  quiet: { fill: 'text-fg3', border: 'border-stroke' },
};

export function StatusBadge({ tone = 'neutral', icon: Icon, bordered = false, title, className = '', children }: {
  tone?: StatusTone;
  icon?: LucideIcon;
  bordered?: boolean;
  title?: string;
  className?: string;
  children: ReactNode;
}) {
  const t = TONE[tone];
  const outlined = bordered || tone === 'quiet';
  return (
    <Tooltip content={title}>
      <span
        className={`shrink-0 inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-caption font-medium whitespace-nowrap ` +
          `${t.fill} ${outlined ? `border ${t.border}` : ''} ${className}`}>
        {Icon && <Icon size={12} aria-hidden className="shrink-0" />}
        {children}
      </span>
    </Tooltip>
  );
}
