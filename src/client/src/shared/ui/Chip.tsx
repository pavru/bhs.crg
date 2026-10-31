import { Icon } from './Icon';
import { Check, Close, KeyboardArrowDown, type IconDef } from './icons';
import type { ButtonHTMLAttributes, ReactNode, Ref } from 'react';
import { Tooltip } from './Tooltip';

/**
 * Чип MD3 ([UI-68], issue #1351): 32 px, форма 8, подпись `label-large`. Вид выводится из свойств, а тип —
 * объединение, которое не пропускает бессмысленных сочетаний (свойства `kind` нет: оно повторяло бы
 * свойства и открывало противоречия вроде «assist, но выбранный»).
 *
 * - **filter** — задан `selected`: переключатель с `aria-pressed`. Выбранный — `secondary-container` без
 *   контура, ведущий знак сменяется галочкой. С `menu` чип открывает выбор (поповер, меню): стрелка справа,
 *   галочки нет, `selected` только красит — нажатым он не бывает, состояние называет подпись (#1432).
 *   Открытое меню поворачивает стрелку — по `data-state=open`, который ставит триггер Radix (чип отбора
 *   `Select variant="chip"`, [UI-69]). Выбранный чип с меню помечен `data-selected`: `aria-pressed` у
 *   него нет, а роль `combobox` состояния «выбран» не знает.
 * - **input** — задан `onRemove`: крестик снятия отдельной кнопкой внутри чипа, с обязательным
 *   `removeLabel` — десять «Снять» в ряду диктор не различит. Замыкающий знак у input один, крестик:
 *   `menu` запрещён, а что чип открывает окно, говорит `aria-haspopup` от триггера Radix. `selected` только
 *   красит. `removeOnly` — жетон значения: нажать в нём нечего, кроме крестика, и текст — подпись (#1469).
 * - **assist** — ни того ни другого: действие или вход в выбор, ведущий знак обязателен (вместо пунктира).
 *
 * Тонов нет: состояние, о котором чип, называет ведущий знак цвета роли — `iconTone` (`error`, `warning`).
 * Цвет без знака снова стал бы тоном, поэтому тип требует `icon`. Контейнер экран не красит. `iconLabel` —
 * имя знака для диктора, когда знак и есть сообщение («нет в справочнике»); иначе он скрыт от диктора.
 *
 * - `count` — число строк под отбором, текстом после подписи (в MD3 счётчика у чипа нет, но число — часть
 *   подписи отбора, а не badge); ноль рисуется как есть, решать, показывать ли чип, — экрану.
 * - `title` — подсказка нашим `Tooltip` у самой кнопки, как у `Button`: у неактивного чипа она и объясняет,
 *   почему нельзя. Внешняя обёртка `<Tooltip><Chip disabled>` этого не умеет — `aria-disabled` вместо
 *   `disabled` подсказка ставит только своей кнопке, и причина пропадала (ревью #1704).
 *
 * Крестик снятия — своя кнопка, а не `IconButton`: та 40 px и раздула бы чип 32 вдвое по ширине. Цель
 * касания 48 по высоте — у каждой кнопки чипа; половины input chip расширяются вбок только наружу, чтобы
 * не отбирать друг у друга видимое ([UI-68]). Цель выходит за чип на 8 px сверху и снизу, поэтому ряд
 * чипов с переносом — с зазором `gap-2` и больше: при меньшем нижний чип отнимает видимое у верхнего.
 * Выбор одного значения из 2–5 вариантов — не ряд чипов, а `SegmentedButton`; условия отбора над таблицей
 * — `shared/filter/FilterChips` (input chips).
 */
type Common = Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'children'> & {
  children: ReactNode;
  ref?: Ref<HTMLButtonElement>;
  count?: ReactNode;
};

/** Ведущий знак: цвет роли и имя — только вместе со знаком. */
type Lead =
  | { icon?: undefined; iconTone?: never; iconLabel?: never }
  | { icon: IconDef; iconTone?: 'error' | 'warning'; iconLabel?: string };

type Filter = { selected: boolean; menu?: boolean; onRemove?: never; removeLabel?: never; removeOnly?: never };
type Input = { onRemove: () => void; removeLabel: string; selected?: boolean; removeOnly?: boolean; menu?: never };
type Assist = { selected?: never; menu?: boolean; onRemove?: never; removeLabel?: never; removeOnly?: never };

export type ChipProps = Common & ((Filter & Lead) | (Input & Lead) | (Assist & Lead & { icon: IconDef }));

const BASE = 'inline-flex items-center h-8 rounded-sm border text-label-large';
/**
 * Состояния — у кнопки, а не у чипа: у input chip их две, и подсвечивается та, что под курсором. Цель
 * касания: чип 32 → 48 (8 px по вертикали, 4 по бокам); у половин input chip — 4 px только с внешней стороны.
 */
const PRESS = 'state-layer focus-ring disabled-content disabled:pointer-events-none touch-target';
const TOUCH_WHOLE = '[--_touch-inset:-8px_-4px]';
const TOUCH_LABEL = '[--_touch-inset:-8px_0_-8px_-4px]';
const TOUCH_REMOVE = '[--_touch-inset:-8px_-4px_-8px_0]';

const UNSELECTED = 'border-outline-variant text-on-surface-variant bg-surface';
const SELECTED = 'border-transparent bg-secondary-container text-on-secondary-container';
const TONE = { error: 'text-error', warning: 'text-warning' } as const;

export function Chip(props: ChipProps) {
  const {
    children, selected, icon, iconTone, iconLabel, count, onRemove, removeLabel, menu = false, removeOnly = false,
    title, className = '', type = 'button', ...rest
  } = props;
  const toggles = selected !== undefined && !menu && !onRemove;
  const shown = toggles && selected ? Check : icon;
  // Ведущий знак — своего цвета: primary у невыбранного (MD3), роль — у знака о состоянии. У отключённого
  // знак гаснет вместе с подписью: цвет берёт у кнопки.
  const leadTone = rest.disabled || shown === Check ? '' : iconTone ? TONE[iconTone] : selected ? '' : 'text-primary';
  // Отключённый по MD3: выбранный — заливка on-surface 12 % без контура, невыбранный — контур 12 %.
  const container = `${BASE} ${selected ? `${SELECTED} disabled-container` : `${UNSELECTED} disabled-outlined`}`;
  const body = (
    <>
      {shown && <Icon icon={shown} size={18} className={`shrink-0 ${leadTone}`}
        {...(iconLabel && shown !== Check ? { 'aria-label': iconLabel } : { 'aria-hidden': true })} />}
      <span className="min-w-0 truncate">{children}</span>
      {count !== undefined && <span className="tabular-nums">· {count}</span>}
      {menu && <Icon icon={KeyboardArrowDown} size={18} aria-hidden
        className="shrink-0 transition-transform ease-standard group-data-[state=open]/chip:rotate-180" />}
    </>
  );
  // Поля по MD3: 16 px, со стороны знака — 8.
  const pad = `${shown ? 'pl-2' : 'pl-4'} ${menu ? 'pr-2' : 'pr-4'}`;

  if (!onRemove)
    return (
      <Tooltip content={title}>
        <button {...rest} type={type} aria-pressed={toggles ? selected : undefined}
          data-selected={menu && selected ? '' : undefined}
          className={`group/chip ${container} ${PRESS} ${TOUCH_WHOLE} gap-2 ${pad} ${className}`}>
          {body}
        </button>
      </Tooltip>
    );

  const labelPad = `${shown ? 'pl-2' : 'pl-3'} pr-1`;
  const label = removeOnly
    // Атрибуты данных (`data-*`) — подписи, а не кнопке, которой нет; ref и обработчики жетону не нужны.
    ? <span {...Object.fromEntries(Object.entries(rest).filter(([k]) => k.startsWith('data-')))}
        className={`inline-flex items-center gap-2 h-full min-w-0 ${labelPad}`}>{body}</span>
    : <Tooltip content={title}>
        <button {...rest} type={type}
          className={`inline-flex items-center gap-2 h-full min-w-0 rounded-l-sm ${labelPad} ${PRESS} ${TOUCH_LABEL}`}>
          {body}
        </button>
      </Tooltip>;

  return (
    // Отключены кнопки, а вид отключённого — у контейнера: data-disabled его включает.
    <span data-disabled={rest.disabled || undefined} className={`${container} ${className}`}>
      {label}
      <Tooltip content={removeLabel}>
        <button type="button" onClick={onRemove} aria-label={removeLabel}
          disabled={rest.disabled}
          className={`h-full pl-0.5 pr-2 rounded-r-sm ${PRESS} ${TOUCH_REMOVE} [--_state-color:var(--color-error)]`}>
          <Icon icon={Close} size={18} aria-hidden />
        </button>
      </Tooltip>
    </span>
  );
}
