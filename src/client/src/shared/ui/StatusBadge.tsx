import { Icon } from './Icon';
import type { IconDef } from './icons';
import type { ReactNode } from 'react';
import { Tooltip } from './Tooltip';

/**
 * Пометка состояния записи (issue #1351): черновик, разобран, оплачен, отклонён, в архиве.
 * Пилюля `text-label-small`; тон — роль из токенов, а не цвет: смысл тона одинаков на всех экранах.
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
  neutral: { fill: 'bg-surface-container text-on-surface-variant', border: 'border-outline-variant' },
  brand: { fill: 'bg-primary-container text-on-primary-container', border: 'border-primary/30' },
  success: { fill: 'bg-success-container text-on-success-container', border: 'border-success/30' },
  warning: { fill: 'bg-warning-container text-on-warning-container', border: 'border-outline-variant' },
  danger: { fill: 'bg-error-container text-on-error-container', border: 'border-error/30' },
  quiet: { fill: 'text-on-surface-variant', border: 'border-outline-variant' },
};

export function StatusBadge({ tone = 'neutral', icon, bordered = false, title, className = '', children }: {
  tone?: StatusTone;
  icon?: IconDef;
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
        className={`shrink-0 inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-label-small font-medium whitespace-nowrap ` +
          `${t.fill} ${outlined ? `border ${t.border}` : ''} ${className}`}>
        {icon && <Icon icon={icon} size={18} aria-hidden className="shrink-0" />}
        {children}
      </span>
    </Tooltip>
  );
}
