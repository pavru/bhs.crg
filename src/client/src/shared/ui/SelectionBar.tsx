import type { ReactNode } from 'react';
import { Button, IconButton } from './Button';
import { Icon } from './Icon';
import { Close, Warning, type IconDef } from './icons';
import { useMedia, WIDE } from '@/shared/hooks/useMedia';

/** Действие над выбранным. На телефоне — `IconButton` с той же подписью: в 375 подпись не влезает. */
export interface SelectionAction {
  label: string;
  icon: IconDef;
  onClick: () => void;
  /** Одно действие — filled, остальные — tonal или outlined. */
  variant?: 'filled' | 'tonal' | 'outlined';
}

/**
 * Контекстная шапка выбора ([UI-81], sharedui-md3-17, макет А развилки Р8) — режим выбора top app bar MD3:
 * пока выбор не пуст, она стоит НА МЕСТЕ шапки области (вкладки, панели, на телефоне — верхней полосы экрана), а
 * не плашкой у низа списка. Шапку вне выбора экран отдаёт `children`, и смену делает компонент. Полоса 64 на
 * `surface-container-high`: ✕ у ведущего края снимает выбор, «Выбрано: N» — `title-large`, действия выбора — у
 * замыкающего (на телефоне — `IconButton`: в 375 подпись не влезает; сворачивает их сам компонент, по описаниям).
 * Полоса липкая к верху прокрутки: выбор делают по всему списку, и действия обязаны быть под рукой и у его конца.
 * Прокрутка с верхним отступом прилипала бы под ним, и строки проезжали бы над полосой — такой экран называет
 * отступ `stickAt` (`-top-4` у `py-4`).
 *
 * <p>Число диктор слышит из живой строки, которая стоит и при пустом выборе: строку, появившуюся сразу с текстом,
 * он не читает, и первый отмеченный флажок прошёл бы молча. Имя полосы для читалки — постоянное, «Выбор строк»:
 * число в имени области переименовывало бы её с каждым флажком.</p>
 *
 * <p>`note` — предупреждение о выборе строкой под полосой, на той же подложке: оно о том, что сделают действия, и
 * должно быть видно до нажатия (часть выбранных связана шире уровня). Число — по тем строкам, над которыми
 * работают действия: скрытые поиском в счёт не идут — это забота экрана.</p>
 */
export function SelectionBar({ count, onClear, clearLabel = 'Снять выбор', actions, note, stickAt = 'top-0', children }: {
  count: number;
  onClear: () => void;
  clearLabel?: string;
  /** Действия над выбранным. */
  actions?: SelectionAction[];
  /** Предупреждение о выборе — под полосой, знаком и цветом `warning`. */
  note?: ReactNode;
  /** Где полоса прилипает: класс `top-*`, минус верхний отступ прокручиваемой области. */
  stickAt?: string;
  /** Шапка области, пока ничего не выбрано. */
  children?: ReactNode;
}) {
  const label = `Выбрано: ${count}`;
  const wide = useMedia(WIDE);
  return (
    <>
      <span aria-live="polite" className="sr-only">{count > 0 ? label : ''}</span>
      {count > 0 ? (
        <div role="region" aria-label="Выбор строк" className={`sticky ${stickAt} z-10 shrink-0 bg-surface-container-high`}>
          <div className="flex items-center gap-2 min-h-16 py-3 pr-4 pl-2">
            <IconButton icon={Close} label={clearLabel} onClick={onClear} />
            <p className="flex-1 min-w-0 ml-1 truncate text-title-large text-on-surface">{label}</p>
            {actions && actions.length > 0 && (
              <div className="flex items-center gap-2 shrink-0">
                {actions.map(a => wide
                  ? <Button key={a.label} variant={a.variant ?? 'tonal'} icon={<Icon icon={a.icon} size={18} />} onClick={a.onClick}>{a.label}</Button>
                  : <IconButton key={a.label} variant={a.variant ?? 'tonal'} label={a.label} icon={a.icon} onClick={a.onClick} />)}
              </div>
            )}
          </div>
          {note && (
            <div className="flex items-start gap-1 px-6 pb-3 text-body-small text-warning">
              <Icon icon={Warning} filled size={18} className="shrink-0" aria-hidden />
              <div className="min-w-0">{note}</div>
            </div>
          )}
        </div>
      ) : children}
    </>
  );
}
