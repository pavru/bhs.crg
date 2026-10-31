import type { ReactNode } from 'react';
import { Icon } from './Icon';
import { Check, type IconDef } from './icons';
import { useRoving } from './rovingFocus';
import { SEGMENT_ROW, segmentClass } from './segmentedClasses';
import { Tooltip } from './Tooltip';

export interface SegmentItem<T extends string> {
  value: T;
  /** Подпись; при `hideLabel` — только имя для диктора и подсказка, видно знак. */
  label: string;
  icon?: IconDef;
  hideLabel?: boolean;
  /** Пометка после подписи (точка «задано»). */
  badge?: ReactNode;
  disabled?: boolean;
  title?: string;
}

/**
 * Сегментный переключатель — segmented button MD3 ([UI-68]): выбор ОДНОГО значения из 2–5 коротких
 * вариантов, без панели. Роли `radiogroup` / `radio` по APG: одна остановка Tab, стрелка выбирает
 * соседний вариант и переводит фокус, Home и End — крайние, отключённый пропускается. `onChange` — выбор,
 * а не просьба: отказать в нём экран не может. Переключатель панели — `Tabs variant="segmented"`: вид тот
 * же (`segmentedClasses.ts`), но роли и клавиатура вкладок. `multiple` нет: потребителя нет.
 *
 * Выбранный сегмент — с галочкой: у сегмента со знаком она становится перед ним, у сегмента из одного
 * знака (`hideLabel`) — рядом, чтобы вариант оставался узнаваем.
 */
export function SegmentedButton<T extends string>({ items, value, onChange, disabled = false, fullWidth = false, className = '', ...aria }: {
  items: SegmentItem<T>[];
  value: T | undefined;
  onChange: (next: T) => void;
  /** Отключён весь ряд. */
  disabled?: boolean;
  /** Ряд во всю ширину, сегменты поровну. */
  fullWidth?: boolean;
  className?: string;
  'aria-label'?: string;
  'aria-labelledby'?: string;
}) {
  const pick = (next: T) => { if (next !== value) onChange(next); };
  const roving = useRoving(items.map(it => it.value), {
    value, disabled: (_, i) => disabled || !!items[i].disabled, select: pick,
  });

  return (
    <div role="radiogroup" onKeyDown={roving.onKeyDown}
      className={`${SEGMENT_ROW}${fullWidth ? ' w-full' : ''} ${className}`} {...aria}>
      {items.map((it, i) => {
        const on = it.value === value;
        const lead = on && !it.hideLabel ? Check : it.icon;
        return (
          <Tooltip key={it.value} content={it.title ?? (it.hideLabel ? it.label : undefined)}>
            <button type="button" role="radio" aria-checked={on} {...roving.item(i)}
              aria-label={it.hideLabel ? it.label : undefined}
              disabled={disabled || it.disabled}
              onClick={() => pick(it.value)}
              className={segmentClass({ selected: on, first: i === 0, last: i === items.length - 1, fill: fullWidth })}>
              {on && it.hideLabel && <Icon icon={Check} size={18} aria-hidden className="shrink-0" />}
              {lead && <Icon icon={lead} size={18} aria-hidden className="shrink-0" />}
              {!it.hideLabel && <span className="min-w-0 truncate">{it.label}</span>}
              {it.badge}
            </button>
          </Tooltip>
        );
      })}
    </div>
  );
}
