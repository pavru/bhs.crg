import type { ReactNode } from 'react';
import { NARROW, useMedia } from '@/shared/hooks/useMedia';
import { Icon } from './Icon';
import { Check, type IconDef } from './icons';
import { useRoving } from './rovingFocus';
import { SEGMENT_ROW, segmentClass } from './segmentedClasses';
import { Tooltip } from './Tooltip';

/**
 * Вкладки (issue #1351) — шаблон APG «tablist» с ручной активацией: стрелки, Home и End двигают
 * ФОКУС, переключает Enter, пробел или щелчок. Так переключение не случается «на проходе»: у
 * редактора документа уход с вкладки спрашивает про несохранённое, и стрелка не должна его вызывать.
 *
 * Вкладки управляемые: `onChange` — ПРОСЬБА перейти, а не переход. Экран может отказать (спросить
 * про несохранённое и остаться) — тогда он просто не меняет `value`.
 *
 * Два вида: `underline` — разделы содержимого и длинные разделы страницы (редактор документа, шаблоны),
 * `segmented` — переключатель панели внутри окна или полосы (выбор ссылки, взгляд затрат). Вид
 * `segmented` рисуется как `SegmentedButton` (`segmentedClasses.ts`, [UI-68]), а роли и клавиатура
 * остаются вкладок; на телефоне (ниже `md`) он становится `underline` — ряд сегментов шире экрана.
 * Панель содержимого связывается с вкладкой через `TabPanel` с тем же `id`. Выбор
 * значения без панели (суммы с НДС или без, точность даты) — не вкладки, а `SegmentedButton`.
 */
export interface TabItem<T extends string> {
  value: T;
  label: ReactNode;
  icon?: IconDef;
  /** Число или пометка после подписи (счётчик ошибок, точка «не сохранено»). */
  badge?: ReactNode;
  disabled?: boolean;
  title?: string;
}

const tabId = (id: string, value: string) => `${id}-tab-${value}`;
const panelId = (id: string, value: string) => `${id}-panel-${value}`;

const STYLE = {
  // Не влезающий ряд прокручивается вбок, а подписи не переносятся (issue #1384): у редактора документа
  // четыре вкладки, и на телефоне они иначе ломались в две строки и раздвигали страницу. Отсюда и черта
  // под вкладкой без `-mb-px`: прокручиваемый ряд обрезал бы её, наезжающую на линию ряда.
  underline: {
    list: 'flex overflow-x-auto [scrollbar-width:none] border-b border-outline-variant',
    // Кольцо — внутрь: прокручиваемый ряд срезал бы наружное.
    tab: 'inline-flex shrink-0 items-center gap-1.5 h-12 px-4 text-body-medium font-medium whitespace-nowrap border-b-2 ' +
      'state-layer focus-ring-inset',
    on: 'border-primary text-primary',
    off: 'border-transparent text-on-surface-variant',
  },
} as const;

type Variant = 'underline' | 'segmented';

export function Tabs<T extends string>({ id, items, value, onChange, variant = 'underline', className = '', ...aria }: {
  /** Основа идентификаторов вкладок и панелей: `TabPanel` ссылается на неё же. */
  id: string;
  items: TabItem<T>[];
  value: T;
  onChange: (next: T) => void;
  variant?: Variant;
  className?: string;
  'aria-label'?: string;
  'aria-labelledby'?: string;
}) {
  // Ручная активация: стрелки, Home и End двигают только фокус ([UI-68], `useRoving`).
  const roving = useRoving(items.map(it => it.value), {
    value, disabled: (_, i) => !!items[i].disabled, select: false, axis: 'horizontal',
  });
  // На телефоне сегменты — вкладки `underline`: ряд сегментов не прокручивается и шире экрана уже при трёх
  // подписях («Из библиотеки», «Поиск в интернете», «Создать вручную» — ≈ 430 px, [UI-68]).
  const narrow = useMedia(NARROW);
  const segmented = variant === 'segmented' && !narrow;
  const s = STYLE.underline;

  return (
    <div role="tablist" className={`${segmented ? SEGMENT_ROW : s.list} ${className}`} {...aria}>
      {items.map((it, i) => {
        const on = it.value === value;
        // У сегмента выбранный — с галочкой, как у `SegmentedButton`: вид один.
        const icon = segmented && on ? Check : it.icon;
        return (
          <Tooltip key={it.value} content={it.title}>
            <button type="button" role="tab" id={tabId(id, it.value)}
              aria-selected={on} aria-controls={panelId(id, it.value)} {...roving.item(i)}
              disabled={it.disabled}
              onClick={() => { if (!on) onChange(it.value); }}
              onKeyDown={roving.onKeyDown}
              className={segmented
                ? segmentClass({ selected: on, first: i === 0, last: i === items.length - 1 })
                : `${s.tab} ${on ? s.on : s.off} disabled-content disabled:pointer-events-none`}>
              {icon && <Icon icon={icon} size={18} aria-hidden className="shrink-0" />}
              {it.label}
              {it.badge}
            </button>
          </Tooltip>
        );
      })}
    </div>
  );
}

/** Панель содержимого вкладки: роль и связь с вкладкой по `id` и `value`. */
export function TabPanel({ id, value, className, children }: {
  id: string;
  value: string;
  className?: string;
  children: ReactNode;
}) {
  return (
    <div role="tabpanel" id={panelId(id, value)} aria-labelledby={tabId(id, value)} className={className}>
      {children}
    </div>
  );
}
