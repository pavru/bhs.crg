import { useEffect, useId, useRef, useState, type FocusEvent, type KeyboardEvent, type ReactNode, type Ref } from 'react';
import * as Popover from '@radix-ui/react-popover';
import { composeRefs } from '@radix-ui/react-compose-refs';
import { useFieldSuggestions, type FieldSuggestion, type SuggestSource } from '../api/fieldSuggestions';
import { TextField, type TextFieldProps } from './TextField';
import { matchStart } from './suggestMatch';

/**
 * Поле с подсказкой по ранее введённому (эпик #1527, issue #1531, требования UI-1…UI-7 домена
 * `sharedui`). Значение — свободный текст: список лишь предлагает то, что уже вводили в этом поле
 * других записей. Выбор ЗАПИСИ справочника сюда не относится — это `RefPickerModal` и кандидаты.
 *
 * <p>Вид — вариант А макета «Подсказка по введённому» (выбор владельца 23.10.2026): слой `Select` в
 * 4 px под полем, не уже поля. Поведение — APG «combobox, list autocomplete, manual selection»:</p>
 * <ul>
 *   <li><b>Открывается вводом, а не фокусом.</b> Tab по форме из двадцати полей не открывает двадцать
 *     списков. В форме ещё ↓, везде — Alt+↓. В ячейке сетки (`compact`) ↓ при закрытом списке — переход
 *     по строкам: сетка уступает стрелки только `aria-expanded="true"`.</li>
 *   <li><b>Принятие — только явное.</b> Ни один пункт не активен до стрелки; листание текст не меняет и
 *     `onChange` не зовёт (у форм есть «тронуто» и охрана ухода). Enter или щелчок — ровно один
 *     `onChange`. Tab закрывает без принятия: иначе Tab по форме молча подставлял бы чужие значения.</li>
 *   <li><b>Тишина вместо пустоты и отказа.</b> Пусто, 403, 503 — списка нет, без «не найдено».</li>
 *   <li><b>Не слой истории.</b> Список — часть поля, как `Select`: на телефоне первая «Назад» убирает
 *     клавиатуру, поле теряет фокус, и список закрывается вместе с ним.</li>
 * </ul>
 */
export interface SuggestFieldProps
  extends Omit<TextFieldProps, 'value' | 'onChange' | 'role' | 'autoComplete' | 'trailing'> {
  value: string;
  onChange: (value: string) => void;
  /** Чьё поле. `null` — поле не подсказывается (правило `Suggestable`): обычное поле, адрес не зовётся. */
  source: SuggestSource | null;
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
  ref?: Ref<HTMLInputElement>;
}

/** Задержка после последней клавиши: три быстрые клавиши — один запрос (UI-5). */
const SUGGEST_DEBOUNCE_MS = 200;

export function SuggestField(props: SuggestFieldProps) {
  // Поле без источника — обычное: ни запроса, ни подписки на кэш (и без QueryClientProvider вокруг).
  return props.source === null ? <PlainField {...props} /> : <SuggestingField {...props} source={props.source} />;
}

/** Пропы оболочки поля до голого `<input>` клетки не доходят. */
function cellProps(rest: Omit<SuggestFieldProps, 'value' | 'onChange' | 'source' | 'compact' | 'label' | 'ref'>) {
  const { hint: _hint, error: _error, invalid: _invalid, containerClassName: _container, ...cell } = rest;
  return cell;
}

function PlainField({ value, onChange, source: _source, compact = false, arrowOpens: _arrowOpens, label, ref, ...rest }: SuggestFieldProps) {
  return compact
    ? <input {...cellProps(rest)} ref={ref} aria-label={label} value={value} onChange={e => onChange(e.target.value)} />
    : <TextField {...rest} ref={ref} label={label} value={value} onChange={e => onChange(e.target.value)} />;
}

/** Показанный ответ адреса и о чём он: подсветка считается по его `q`, а не по набранному сейчас. */
interface Shown { q: string; items: FieldSuggestion[]; of: string }

function SuggestingField({
  value, onChange, source, compact = false, arrowOpens = !compact, label, onKeyDown, onBlur, className = '', ref, ...rest
}: SuggestFieldProps & { source: SuggestSource }) {
  const listId = `${useId()}-list`;
  const input = useRef<HTMLInputElement>(null);
  /** Человек хочет список: набрал или позвал стрелкой. Виден он, только когда есть что показать. */
  const [open, setOpen] = useState(false);
  /** Отстоявшееся набранное этого открытия — о нём спрашиваем адрес; `null` — ещё не отстоялось. */
  const [asked, setAsked] = useState<string | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined);
  const of = [source.type, source.field, source.scope ?? '', source.scopeId ?? ''].join('|');
  // Клетку отдали другому полю — открытие прежнего кончилось: его набранное о новом поле не спрашивают.
  const [openOf, setOpenOf] = useState(of);
  if (openOf !== of) {
    setOpenOf(of);
    setOpen(false);
    setAsked(null);
  }

  const query = useFieldSuggestions(open ? source : null, open ? asked : null);

  // Пока едет ответ на новое набранное, держится прошлый ответ ЭТОГО ЖЕ открытия и этого же поля —
  // иначе список мигал бы на каждую клавишу. Закрытие, отказ и смена поля его сбрасывают: прошлое
  // открытие и чужое поле — уже другие значения.
  const fresh: Shown | null = open && asked !== null && query.data ? { q: asked, items: query.data, of } : null;
  const [held, setHeld] = useState<Shown | null>(null);
  if (fresh && (held?.items !== fresh.items || held.q !== fresh.q || held.of !== of)) setHeld(fresh);
  if (!fresh && held && (!open || query.isError)) setHeld(null);
  const view = !open || query.isError ? null : fresh ?? (held?.of === of ? held : null);
  const items = view?.items ?? [];
  const shown = items.length > 0;

  // Новый список — снова ни один пункт не активен: активный прошлого ответа указывал бы на другое.
  const [activeIn, setActiveIn] = useState<FieldSuggestion[] | undefined>(undefined);
  const [active, setActive] = useState(-1);
  if (activeIn !== view?.items) {
    setActiveIn(view?.items);
    setActive(-1);
  }

  useEffect(() => {
    const pending = timer;
    return () => clearTimeout(pending.current);
  }, []);

  function close() {
    clearTimeout(timer.current);
    setOpen(false);
    setAsked(null);
  }

  /** Открыть сразу, без задержки: ↓ в форме или Alt+↓ (и в пустом поле — верх по полю). */
  function openNow() {
    clearTimeout(timer.current);
    setAsked(value.trim());
    setOpen(true);
  }

  function type(next: string) {
    onChange(next);
    clearTimeout(timer.current);
    // Пункт выбирали для прежнего текста: Enter после дописанного не должен подставить его молча.
    setActive(-1);
    if (next.trim() === '') { close(); return; }
    setOpen(true);
    timer.current = setTimeout(() => setAsked(next.trim()), SUGGEST_DEBOUNCE_MS);
  }

  function accept(item: FieldSuggestion) {
    close();
    onChange(item.value);
    // Каретка — в конец принятого: дальше человек дописывает или уходит Tab.
    requestAnimationFrame(() => input.current?.setSelectionRange(item.value.length, item.value.length));
  }

  function keyDown(e: KeyboardEvent<HTMLInputElement>) {
    onKeyDown?.(e);
    if (e.defaultPrevented || e.nativeEvent.isComposing) return;
    if (e.key === 'ArrowDown' && e.altKey) {
      e.preventDefault();
      if (!shown) openNow();
      return;
    }
    if (!shown) {
      if (e.key === 'ArrowDown' && arrowOpens && !e.ctrlKey && !e.metaKey && !e.shiftKey) {
        e.preventDefault();
        openNow();
      }
      // Список ещё не показан, а человек уходит — ответ, пришедший потом, его уже не догонит.
      if (e.key === 'Escape' || e.key === 'Tab') close();
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
        if (active < 0) { close(); break; }
        // Не отправить форму и не сохранить окно: Enter здесь — выбор пункта.
        e.preventDefault();
        accept(items[active]);
        break;
      case 'Tab':
        close();
        break;
      case 'Escape':
        // Закрыть только список: окно, в котором стоит поле, остаётся.
        e.preventDefault();
        e.stopPropagation();
        close();
        break;
    }
  }

  function blur(e: FocusEvent<HTMLInputElement>) {
    onBlur?.(e);
    close();
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
  } as const;
  // Живая область — внутри оболочки поля: соседом в разметке экрана поле перестало бы быть последним
  // в стопке `space-y-*`. У клетки оболочки нет — там она ложится рядом с полем, в клетку.
  const announce = <Announce shown={shown} count={items.length} />;

  return (
    <Popover.Root open={shown} onOpenChange={next => { if (!next) close(); }}>
      <Popover.Anchor asChild>
        {compact
          ? <input {...cellProps(rest)} {...combobox} ref={composeRefs(input, ref)} aria-label={label} className={className} />
          : <TextField {...rest} {...combobox} ref={composeRefs(input, ref)} label={label} className={className}>{announce}</TextField>}
      </Popover.Anchor>
      {compact && announce}
      <Popover.Portal>
        <Popover.Content id={listId} role="listbox" aria-label={`Введённое ранее: ${label}`}
          side="bottom" align="start" sideOffset={4} collisionPadding={8} hideWhenDetached
          // Фокус остаётся в поле: пункты его не берут, выбранный стрелкой — `aria-activedescendant`.
          onOpenAutoFocus={e => e.preventDefault()} onCloseAutoFocus={e => e.preventDefault()}
          // Щелчок по самому полю — не «мимо»: список закрыл бы и тут же открылся бы вводом.
          onInteractOutside={e => { if (e.target === input.current) e.preventDefault(); }}
          className="z-50 overflow-hidden rounded-md border border-stroke bg-surface p-1 shadow-[var(--f-shadow16)]"
          // Не уже поля; в узкой клетке сетки — не уже 18rem, чтобы марка кабеля не ложилась в столбик.
          style={{ width: 'max(var(--radix-popover-trigger-width), 18rem)', maxWidth: 'min(32rem, calc(100vw - 32px))' }}>
          <SuggestOptions items={items} q={view?.q ?? ''} listId={listId} active={active} onPick={accept} />
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}

function SuggestOptions({ items, q, listId, active, onPick }: {
  items: FieldSuggestion[]; q: string; listId: string; active: number; onPick: (item: FieldSuggestion) => void;
}) {
  // Разделитель — только когда есть обе группы: у записи без стройки все «чужие», и черта там врала бы.
  const split = items.some(i => i.here) && items.some(i => !i.here) ? items.findIndex(i => !i.here) : -1;
  return items.map((item, i) => (
    <Item key={item.value} split={i === split}>
      <div id={`${listId}-${i}`} role="option" aria-selected={i === active} title={item.value}
        // Щелчок не уводит фокус из поля: иначе blur закрыл бы список раньше щелчка.
        onMouseDown={e => e.preventDefault()} onClick={() => onPick(item)}
        className={'flex min-h-8 cursor-pointer select-none items-center rounded px-3 py-1.5 text-sm text-fg1 ' +
          'pointer-coarse:min-h-11 ' + (i === active ? 'bg-brand-subtle text-brand-hover' : 'hover:bg-muted')}>
        <span className="line-clamp-2">{highlight(item.value, q)}</span>
      </div>
    </Item>
  ));
}

function Item({ split, children }: { split: boolean; children: ReactNode }) {
  if (!split) return children;
  return (
    <>
      <div role="presentation" className="mt-1 border-t border-stroke-subtle px-3 pb-1 pt-2 text-caption text-fg4">
        С других строек
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
