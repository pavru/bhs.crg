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
 * Три вида. `primary` — разделы верхнего уровня области (primary tabs MD3, [UI-90]): «Редакции | Приложения» в
 * карточке сметы. Под ним — только `underline`, в `TabPanel` ряда `primary`, со своим `id`; внутри `underline`
 * или `segmented` ряд `primary` не ставится ([UI-91]). «Ещё N» у него нет: верхний уровень — 2–4 раздела.
 * `underline` — это MD3 secondary: разделы содержимого и длинные разделы страницы (редактор документа, шаблоны),
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

/**
 * Вид `underline`; сегменты рисует `segmentClass`. Не влезающий ряд прокручивается вбок, а подписи не
 * переносятся (issue #1384): у редактора документа четыре вкладки, и на телефоне они иначе ломались в две
 * строки и раздвигали страницу. Отсюда и черта под вкладкой без `-mb-px`: прокручиваемый ряд обрезал бы
 * её, наезжающую на линию ряда.
 */
const UNDERLINE = {
  list: 'flex overflow-x-auto [scrollbar-width:none] border-b border-outline-variant',
  // Кольцо — внутрь: прокручиваемый ряд срезал бы наружное.
  tab: 'inline-flex shrink-0 items-center gap-1.5 h-12 px-4 text-body-medium font-medium whitespace-nowrap border-b-2 ' +
    'state-layer focus-ring-inset',
  on: 'border-primary text-primary',
  off: 'border-transparent text-on-surface-variant',
} as const;

/**
 * Вид `primary` ([UI-90]). Индикатор — не рамка кнопки, а полоса внутри обёртки содержимого: у primary MD3 он
 * шириной по содержимому (значок, подпись, счётчик), а рамка кнопки дала бы всю вкладку с полями — это вид
 * secondary. Радиус 3 dp из MD3 в шкале формы нет — ближайшая ступень, `xs` (4 px). Со значком значок стоит над
 * подписью и ряд выше — 64 вместо 48; решает наличие значка у любой вкладки, чтобы высоты в ряду не расходились,
 * а вкладка без значка держит под него пустое место — иначе её подпись встала бы по центру, выше соседних.
 * Ниже `md` вкладки делят ряд поровну (fixed tabs MD3), но не уже своего содержимого — иначе ряд прокручивается;
 * поэтому `shrink-0` у вкладки только на рабочем столе: с `flex-1` он спорил бы о сжатии порядком правил в CSS.
 * Ряд — тот же, что у `underline` (`UNDERLINE.list`).
 */
const PRIMARY = {
  tab: 'inline-flex justify-center px-4 text-label-large whitespace-nowrap state-layer focus-ring-inset',
  flat: 'h-12',
  stacked: 'h-16',
  fill: 'flex-1 min-w-max',
  fit: 'shrink-0',
  on: 'text-primary',
  off: 'text-on-surface-variant',
  content: 'relative inline-flex min-w-6 items-center justify-center',
  flatContent: 'gap-1.5',
  stackedContent: 'flex-col gap-0.5',
  indicator: 'absolute inset-x-0 bottom-0 h-0.75 rounded-t-xs bg-primary',
  iconSlot: 'size-6 shrink-0',
} as const;

/** Недоступная вкладка (`primary` и `underline`; у сегмента — свой вид в `segmentClass`). */
const DISABLED = 'disabled-content disabled:pointer-events-none';

/** Есть ли что показать в `badge`: `0` — показать, пусто и `false` — нет. */
const hasBadge = (badge: ReactNode) => badge !== undefined && badge !== null && badge !== false && badge !== '';

type Variant = 'primary' | 'underline' | 'segmented';

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
  const primary = variant === 'primary';
  const stacked = primary && items.some(it => it.icon);
  const s = UNDERLINE;
  const p = PRIMARY;

  return (
    <div role="tablist" className={`${segmented ? SEGMENT_ROW : s.list} ${className}`} {...aria}>
      {items.map((it, i) => {
        const on = it.value === value;
        // У сегмента выбранный — с галочкой, как у `SegmentedButton`: вид один.
        const icon = segmented && on ? Check : it.icon;
        // Пробел между подписью и счётчиком — для диктора: без него имя вкладки «Приложения3». В строке flex
        // пробельный текст не рисуется, вид не меняется.
        const badge = hasBadge(it.badge) ? <>{' '}{it.badge}</> : null;
        const look = segmented
          ? segmentClass({ selected: on, first: i === 0, last: i === items.length - 1 })
          : primary
            ? `${p.tab} ${stacked ? p.stacked : p.flat} ${narrow ? p.fill : p.fit} ${on ? p.on : p.off} ${DISABLED}`
            : `${s.tab} ${on ? s.on : s.off} ${DISABLED}`;
        return (
          <Tooltip key={it.value} content={it.title}>
            <button type="button" role="tab" id={tabId(id, it.value)}
              // Панель рисуется только у выбранной вкладки (`TabPanel` с текущим `value`): ссылка у остальных
              // вела бы на элемент, которого нет.
              aria-selected={on} aria-controls={on ? panelId(id, it.value) : undefined} {...roving.item(i)}
              disabled={it.disabled}
              onClick={() => { if (!on) onChange(it.value); }}
              onKeyDown={roving.onKeyDown}
              className={look}>
              {primary ? (
                <span data-tab-content className={`${p.content} ${stacked ? p.stackedContent : p.flatContent}`}>
                  {icon ? <Icon icon={icon} size={24} aria-hidden className="shrink-0" />
                    : stacked && <span data-tab-icon-slot aria-hidden className={p.iconSlot} />}
                  <span className="inline-flex items-center gap-1.5">{it.label}{badge}</span>
                  {on && <span data-tab-indicator aria-hidden className={p.indicator} />}
                </span>
              ) : (
                <>
                  {icon && <Icon icon={icon} size={18} aria-hidden className="shrink-0" />}
                  {it.label}
                  {badge}
                </>
              )}
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
