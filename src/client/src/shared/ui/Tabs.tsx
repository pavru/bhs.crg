import { useRef, type KeyboardEvent, type ReactNode } from 'react';
import { Icon } from './Icon';
import type { IconDef } from './icons';
import { Tooltip } from './Tooltip';

/**
 * Вкладки (issue #1351) — шаблон APG «tablist» с ручной активацией: стрелки, Home и End двигают
 * ФОКУС, переключает Enter, пробел или щелчок. Так переключение не случается «на проходе»: у
 * редактора документа уход с вкладки спрашивает про несохранённое, и стрелка не должна его вызывать.
 *
 * Вкладки управляемые: `onChange` — ПРОСЬБА перейти, а не переход. Экран может отказать (спросить
 * про несохранённое и остаться) — тогда он просто не меняет `value`.
 *
 * Два вида: `underline` — разделы содержимого (редактор документа), `segmented` — переключатель
 * режима внутри панели или окна (шаблоны, выбор ссылки). Панель содержимого связывается с вкладкой
 * через `TabPanel` с тем же `id`.
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
    tab: 'inline-flex shrink-0 items-center gap-1.5 h-12 px-4 text-sm font-medium whitespace-nowrap border-b-2 ' +
      'state-layer focus-ring-inset',
    on: 'border-primary text-primary',
    off: 'border-transparent text-on-surface-variant',
  },
  segmented: {
    list: 'inline-flex bg-surface-container rounded-lg p-0.5',
    tab: 'inline-flex items-center gap-1.5 px-3 py-1.5 text-sm rounded-md state-layer focus-ring',
    on: 'bg-surface text-on-surface font-medium shadow-sm',
    off: 'text-on-surface-variant',
  },
} as const;

export function Tabs<T extends string>({ id, items, value, onChange, variant = 'underline', className = '', ...aria }: {
  /** Основа идентификаторов вкладок и панелей: `TabPanel` ссылается на неё же. */
  id: string;
  items: TabItem<T>[];
  value: T;
  onChange: (next: T) => void;
  variant?: keyof typeof STYLE;
  className?: string;
  'aria-label'?: string;
  'aria-labelledby'?: string;
}) {
  const refs = useRef<(HTMLButtonElement | null)[]>([]);
  const s = STYLE[variant];

  function onKey(e: KeyboardEvent, i: number) {
    const enabled = items.map((it, k) => (it.disabled ? -1 : k)).filter(k => k >= 0);
    const at = enabled.indexOf(i);
    let next: number;
    if (e.key === 'ArrowRight') next = enabled[(at + 1) % enabled.length];
    else if (e.key === 'ArrowLeft') next = enabled[(at - 1 + enabled.length) % enabled.length];
    else if (e.key === 'Home') next = enabled[0];
    else if (e.key === 'End') next = enabled[enabled.length - 1];
    else return;
    e.preventDefault();
    refs.current[next]?.focus();
  }

  return (
    <div role="tablist" className={`${s.list} ${className}`} {...aria}>
      {items.map((it, i) => {
        const on = it.value === value;
        const icon = it.icon;
        return (
          <Tooltip key={it.value} content={it.title}>
            <button type="button" role="tab" id={tabId(id, it.value)}
              aria-selected={on} aria-controls={panelId(id, it.value)} tabIndex={on ? 0 : -1}
              disabled={it.disabled}
              ref={el => { refs.current[i] = el; }}
              onClick={() => { if (!on) onChange(it.value); }}
              onKeyDown={e => onKey(e, i)}
              className={`${s.tab} ${on ? s.on : s.off} disabled-content disabled:pointer-events-none`}>
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
