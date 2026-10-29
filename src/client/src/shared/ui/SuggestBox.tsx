import { useId, useRef, useState, type FocusEvent, type KeyboardEvent, type ReactNode, type Ref } from 'react';
import * as Popover from '@radix-ui/react-popover';
import { composeRefs } from '@radix-ui/react-compose-refs';
import { TextField, type TextFieldProps } from './TextField';
import { matchStart, type SuggestItem } from './suggestMatch';
import type { SuggestFeed } from './suggestFeeds';

/** Пропы поля с подсказкой — общие для обоих источников (`SuggestField`). */
export interface SuggestFieldBaseProps
  extends Omit<TextFieldProps, 'value' | 'onChange' | 'role' | 'autoComplete' | 'trailing'> {
  value: string;
  onChange: (value: string) => void;
  /**
   * Ячейка сетки: без рамки и подписи в вырезе (клетку рисует сетка), `label` — `aria-label`; ↓ при
   * закрытом списке остаётся сетке, если не задан `arrowOpens`.
   */
  compact?: boolean;
  /**
   * ↓ при закрытом списке открывает его. По умолчанию — вне сетки (`!compact`): в клетке стрелка — переход
   * по строкам. Голое поле ФОРМЫ (`compact` без сетки вокруг) передаёт `true` — ведёт себя как поле с
   * подписью рядом, а не как клетка.
   */
  arrowOpens?: boolean;
  /** Подсказка в пустом поле — только у клетки (`compact`): у поля формы вместо неё подпись в вырезе. */
  placeholder?: string;
  /**
   * Пункт принят — Enter или щелчком, уже после `onChange` с его значением. Нужен полю, где выбор пункта
   * — сразу действие: в «из списка» отбора принятый пункт тут же уходит в список значений.
   */
  onAccept?: (value: string) => void;
  ref?: Ref<HTMLInputElement>;
}

/** Пропы оболочки поля до голого `<input>` клетки не доходят. */
function cellProps(rest: Omit<SuggestFieldBaseProps, 'value' | 'onChange' | 'compact' | 'label' | 'ref' | 'arrowOpens' | 'onAccept' | 'placeholder'>) {
  const { hint: _hint, error: _error, invalid: _invalid, containerClassName: _container, ...cell } = rest;
  return cell;
}

/** Поле без подсказки — то же поле, без списка. */
export function PlainField({
  value, onChange, compact = false, arrowOpens: _arrowOpens, onAccept: _onAccept, placeholder, label, ref, ...rest
}: SuggestFieldBaseProps) {
  return compact
    ? <input {...cellProps(rest)} ref={ref} aria-label={label} placeholder={placeholder} value={value}
        onChange={e => onChange(e.target.value)} />
    : <TextField {...rest} ref={ref} label={label} value={value} onChange={e => onChange(e.target.value)} />;
}

/**
 * Поле со списком подсказки — общее для адреса и перечня (UI-1…UI-8, UI-55): слой, клавиатура, `aria-*`,
 * живая область и подсветка. Откуда пункты, решает лента (`SuggestFeed`); правила поведения — в описании
 * `SuggestField`.
 */
export function SuggestBox({
  value, onChange, onAccept, feed, compact = false, arrowOpens = !compact, placeholder, label, onKeyDown, onBlur, className = '',
  ref, ...rest
}: SuggestFieldBaseProps & { feed: SuggestFeed }) {
  const listId = `${useId()}-list`;
  const input = useRef<HTMLInputElement>(null);
  const { view } = feed;
  const items = view?.items ?? [];
  const shown = items.length > 0;

  // Новый список — снова ни один пункт не активен: активный прошлого списка указывал бы на другое. Новый
  // — по содержимому, а не по ссылке: перечень, собранный заново тем же, выбор стрелкой не сбрасывает.
  const content = view ? [view.q, ...items.map(i => i.value)].join('\n') : null;
  const [activeIn, setActiveIn] = useState<string | null>(null);
  const [active, setActive] = useState(-1);
  if (activeIn !== content) {
    setActiveIn(content);
    setActive(-1);
  }

  function type(next: string) {
    onChange(next);
    // Пункт выбирали для прежнего текста: Enter после дописанного не должен подставить его молча.
    setActive(-1);
    feed.typed(next);
  }

  function accept(item: SuggestItem) {
    feed.close();
    onChange(item.value);
    onAccept?.(item.value);
    // Каретка — в конец принятого: дальше человек дописывает или уходит Tab.
    requestAnimationFrame(() => input.current?.setSelectionRange(item.value.length, item.value.length));
  }

  function keyDown(e: KeyboardEvent<HTMLInputElement>) {
    onKeyDown?.(e);
    // Enter забрал владелец поля (добавил набранное в список) — список своё отслужил.
    if (e.defaultPrevented && e.key === 'Enter') feed.close();
    if (e.defaultPrevented || e.nativeEvent.isComposing) return;
    if (e.key === 'ArrowDown' && e.altKey) {
      e.preventDefault();
      if (!shown) feed.openNow();
      return;
    }
    if (!shown) {
      if (e.key === 'ArrowDown' && arrowOpens && !e.ctrlKey && !e.metaKey && !e.shiftKey) {
        e.preventDefault();
        feed.openNow();
      }
      // Список ещё не показан, а человек уходит — ответ, пришедший потом, его уже не догонит.
      if (e.key === 'Escape' || e.key === 'Tab') feed.close();
      return;
    }
    switch (e.key) {
      case 'ArrowDown':
        e.preventDefault();
        setActive(a => Math.min(a + 1, items.length - 1));
        break;
      case 'ArrowUp':
        // С первого — снова «ни один»: в поле то, что набрано.
        e.preventDefault();
        setActive(a => (a <= 0 ? -1 : a - 1));
        break;
      case 'Enter':
        if (active < 0) { feed.close(); break; }
        // Не отправить форму и не сохранить окно: Enter здесь — выбор пункта.
        e.preventDefault();
        accept(items[active]);
        break;
      case 'Tab':
        feed.close();
        break;
      case 'Escape':
        // Закрыть только список: окно, в котором стоит поле, остаётся.
        e.preventDefault();
        e.stopPropagation();
        feed.close();
        break;
    }
  }

  function blur(e: FocusEvent<HTMLInputElement>) {
    onBlur?.(e);
    feed.close();
  }

  const combobox = {
    role: 'combobox',
    'aria-autocomplete': 'list',
    'aria-expanded': shown,
    'aria-controls': listId,
    'aria-activedescendant': shown && active >= 0 ? `${listId}-${active}` : undefined,
    autoComplete: 'off',
    value,
    onChange: (e: { target: { value: string } }) => type(e.target.value),
    onKeyDown: keyDown,
    onBlur: blur,
    // Пока список открыт, описание поля не зачитывается: человек выбирает пункт, а описание — о набранном
    // (пометку отбора под полем в это время и не видно, UI-56).
    ...(shown && rest['aria-describedby'] ? { 'aria-describedby': undefined } : {}),
  } as const;
  // Живая область — внутри оболочки поля: соседом в разметке экрана поле перестало бы быть последним
  // в стопке `space-y-*`. У клетки оболочки нет — там она ложится рядом с полем, в клетку.
  const announce = <Announce shown={shown} count={items.length} />;

  return (
    <Popover.Root open={shown} onOpenChange={next => { if (!next) feed.close(); }}>
      <Popover.Anchor asChild>
        {compact
          ? <input {...cellProps(rest)} {...combobox} ref={composeRefs(input, ref)} aria-label={label} placeholder={placeholder}
              className={className} />
          : <TextField {...rest} {...combobox} ref={composeRefs(input, ref)} label={label} className={className}>{announce}</TextField>}
      </Popover.Anchor>
      {compact && announce}
      <Popover.Portal>
        <Popover.Content id={listId} role="listbox" aria-label={feed.listLabel}
          side="bottom" align="start" sideOffset={4} collisionPadding={8} hideWhenDetached
          // Фокус остаётся в поле: пункты его не берут, выбранный стрелкой — `aria-activedescendant`.
          onOpenAutoFocus={e => e.preventDefault()} onCloseAutoFocus={e => e.preventDefault()}
          // Щелчок по самому полю — не «мимо»: список закрыл бы и тут же открылся бы вводом.
          onInteractOutside={e => { if (e.target === input.current) e.preventDefault(); }}
          className="z-50 overflow-hidden rounded-md border border-outline-variant bg-surface p-1 shadow-[var(--md-sys-elevation-level2)]"
          // Не уже поля; в узкой клетке сетки — не уже 18rem, чтобы марка кабеля не ложилась в столбик.
          style={{ width: 'max(var(--radix-popover-trigger-width), 18rem)', maxWidth: 'min(32rem, calc(100vw - 32px))' }}>
          <SuggestOptions items={items} q={view?.q ?? ''} listId={listId} active={active} onPick={accept} />
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}

function SuggestOptions({ items, q, listId, active, onPick }: {
  items: SuggestItem[]; q: string; listId: string; active: number; onPick: (item: SuggestItem) => void;
}) {
  return items.map((item, i) => (
    <Item key={item.value} group={item.group && item.group !== items[i - 1]?.group ? item.group : undefined} first={i === 0}>
      <div id={`${listId}-${i}`} role="option" aria-selected={i === active} title={item.label ?? item.value}
        // Щелчок не уводит фокус из поля: иначе blur закрыл бы список раньше щелчка.
        onMouseDown={e => e.preventDefault()} onClick={() => onPick(item)}
        className={'flex min-h-8 cursor-pointer select-none items-center gap-3 rounded px-3 py-1.5 text-sm text-on-surface ' +
          'pointer-coarse:min-h-11 ' + (i === active ? 'bg-primary-container text-brand-hover' : 'hover:bg-surface-container')}>
        <span className="line-clamp-2">{highlight(item.label ?? item.value, q)}</span>
        {item.note && <span className="ml-auto shrink-0 text-caption text-on-surface-variant">{item.note}</span>}
      </div>
    </Item>
  ));
}

/** Пункт с подписью своей группы над ним — у первого пункта группы в списке. */
function Item({ group, first, children }: { group: string | undefined; first: boolean; children: ReactNode }) {
  if (!group) return children;
  return (
    <>
      <div role="presentation"
        className={`px-3 pb-1 pt-2 text-caption text-on-surface-variant ${first ? '' : 'mt-1 border-t border-outline-variant'}`}>
        {group}
      </div>
      {children}
    </>
  );
}

/**
 * Число подсказок — читалке, и только при открытии: объявление на каждое обновление проговаривало бы
 * каждую клавишу.
 */
function Announce({ shown, count }: { shown: boolean; count: number }) {
  const [said, setSaid] = useState({ shown: false, text: '' });
  if (said.shown !== shown) setSaid({ shown, text: shown ? `Подсказок: ${count}` : '' });
  return <span aria-live="polite" className="sr-only">{said.text}</span>;
}

function highlight(value: string, q: string): ReactNode {
  const at = matchStart(value, q);
  if (at < 0) return value;
  const end = at + q.trim().length;
  return <>{value.slice(0, at)}<span className="font-semibold">{value.slice(at, end)}</span>{value.slice(end)}</>;
}
