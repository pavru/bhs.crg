import type { ReactNode } from 'react';
import { Icon } from './Icon';
import { CheckCircle, ErrorIcon, Warning, type FilledIconDef, type IconDef } from './icons';
import { Tooltip } from './Tooltip';

/**
 * Пометка состояния записи ([UI-74], [UI-43]): черновик, разобран, оплачен, отклонён, в архиве. Текст
 * `label-medium` цвета роли — без заливки и рамки: пилюля пометки (бывший `StatusBadge`) в MD3 не
 * значится, а в плотной строке спорила с кнопками. Отступление от MD3 (D8 зонтика `sharedui-md3-00-epic`)
 * — ведущий знак.
 *
 * - `neutral` — обычное рабочее состояние без оценки (черновик, заперт);
 * - `primary` — в работе, передано дальше;
 * - `success` — доведено (оплачен, проведена, сформирован);
 * - `warning` — требует внимания, но не ошибка (идёт, не найдено);
 * - `error`   — ошибка или потеря (не сформирован, запись удалена);
 * - `quiet`   — закрыто без последствий (отклонено, в архиве).
 *
 * <p>У `success`, `warning` и `error` — ведущий залитый знак: состояние не передаётся только цветом.
 * `icon` его заменяет — так разные смыслы одного тона различаются знаком, а не одним словом (отчёт
 * проверки связок). Знак 18 px — ступень шкалы [UI-60] «в тексте и строке». Соседние пометки разделяет «·»
 * — сама пометка, без обёртки.</p>
 *
 * <p>Число у подписи — `CountText`, на знаке — `Badge`: пометка счётчиком не бывает.</p>
 */
export type StatusTone = 'neutral' | 'primary' | 'success' | 'warning' | 'error' | 'quiet';

const TONE: Record<StatusTone, { text: string; icon?: FilledIconDef }> = {
  neutral: { text: 'text-on-surface-variant' },
  primary: { text: 'text-primary' },
  success: { text: 'text-success', icon: CheckCircle },
  warning: { text: 'text-warning', icon: Warning },
  error: { text: 'text-error', icon: ErrorIcon },
  quiet: { text: 'text-on-surface-variant' },
};

/** Знак залитым, если у него есть заливка в наборе, — иначе контуром. */
function Mark({ icon }: { icon: IconDef }) {
  return icon.fill
    ? <Icon icon={icon as FilledIconDef} filled size={18} className="shrink-0" />
    : <Icon icon={icon} size={18} className="shrink-0" />;
}

export function StatusText({ tone = 'neutral', icon, title, className = '', children }: {
  tone?: StatusTone;
  icon?: IconDef;
  title?: string;
  className?: string;
  children: ReactNode;
}) {
  const t = TONE[tone];
  const mark = icon ?? t.icon;
  return (
    <Tooltip content={title}>
      <span
        className={`shrink-0 inline-flex items-center gap-1 text-label-medium whitespace-nowrap ${t.text} ` +
          `[&+&]:ms-1 [&+&]:before:content-['·'] [&+&]:before:text-on-surface-variant ${className}`}>
        {mark && <Mark icon={mark} />}
        {children}
      </span>
    </Tooltip>
  );
}
