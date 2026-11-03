import type { ReactNode } from 'react';
import { IconButton } from './Button';
import { Icon } from './Icon';
import { Close, Warning } from './icons';

/**
 * Контекстная шапка выбора ([UI-81], sharedui-md3-17, макет А развилки Р8) — режим выбора top app bar MD3:
 * пока выбор не пуст, она стоит НА МЕСТЕ шапки области (вкладки, панели, на телефоне — верхней полосы экрана), а
 * не плашкой у низа списка. Полоса 64 на `surface-container-high`: ✕ у ведущего края снимает выбор, «Выбрано: N»
 * — `title-large`, действия выбора — у замыкающего (на телефоне — `IconButton`: в 375 подпись не влезает).
 *
 * <p>`note` — предупреждение о выборе строкой под полосой, на той же подложке: оно о том, что сделают действия, и
 * должно быть видно до нажатия (часть выбранных связана шире уровня). Число — по тем строкам, над которыми
 * работают действия: скрытые поиском в счёт не идут — это забота экрана.</p>
 */
export function SelectionBar({ count, onClear, clearLabel = 'Снять выбор', actions, note }: {
  count: number;
  onClear: () => void;
  clearLabel?: string;
  /** Действия над выбранным: одна filled, остальные — tonal, outlined или text. */
  actions?: ReactNode;
  /** Предупреждение о выборе — под полосой, знаком и цветом `warning`. */
  note?: ReactNode;
}) {
  const label = `Выбрано: ${count}`;
  return (
    <div role="region" aria-label={label} className="shrink-0 bg-surface-container-high">
      <div className="flex items-center gap-2 min-h-16 py-3 pr-4 pl-2">
        <IconButton icon={Close} label={clearLabel} onClick={onClear} />
        {/* Число меняется на месте — диктор слышит его, не уходя с флажка строки. */}
        <p aria-live="polite" className="flex-1 min-w-0 ml-1 truncate text-title-large text-on-surface">{label}</p>
        {actions && <div className="flex items-center gap-2 shrink-0">{actions}</div>}
      </div>
      {note && (
        <p className="flex items-start gap-1 px-6 pb-3 text-body-small text-warning">
          <Icon icon={Warning} filled size={18} className="shrink-0" aria-hidden />
          <span className="min-w-0">{note}</span>
        </p>
      )}
    </div>
  );
}
