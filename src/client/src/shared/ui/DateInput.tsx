import { useEffect, useRef, useState } from 'react';
import * as Popover from '@radix-ui/react-popover';
import { AlertCircle, CalendarDays } from 'lucide-react';
import type { DatePrecision } from '@/shared/api/types';
import { Calendar } from './Calendar';
import { Tooltip } from './Tooltip';
import { EMPTY_SEGMENTS, SEGMENT_PROBLEMS, fullYear, parsePasted, readSegments, segmentsOf, type Segments } from './dateSegments';

interface DateInputProps {
  /** Stored value: ISO YYYY-MM-DD or '' (always full ISO, независимо от точности) */
  value: string;
  /**
   * Emits full ISO YYYY-MM-DD when the visible segments make a real date, '' when explicitly cleared,
   * nothing while partially typed or when the date does not exist (31.02, issue #1460). Скрытые
   * точностью части допоняются '01' (issue #60): 'year' → YYYY-01-01, 'month' → YYYY-MM-01.
   */
  onChange: (iso: string) => void;
  /**
   * Ошибка набранного — текстом для подписи под полем, null — ошибки нет (issue #1460): «Такой даты
   * нет», «Дата не дописана» (после ухода из поля), значение не дата. Задан — ошибку рисует
   * вызывающий (`DateField`); не задан — поле само красит цифры и ставит значок с подсказкой.
   */
  onProblem?: (message: string | null) => void;
  /** Точность ввода: 'day' (ДД.ММ.ГГГГ, по умолч.), 'month' (ММ.ГГГГ), 'year' (ГГГГ). */
  precision?: DatePrecision;
  /** Applied to the outer container div (border, bg, padding, focus-within:ring, width) */
  className?: string;
  disabled?: boolean;
  /**
   * Значение показано, но не правится (issue #958) — в отличие от `disabled`, поле остаётся в
   * обходе клавиатурой, а сегменты можно выделить и скопировать. Так рисуются поля, значение
   * которых кладёт не человек: привязка к источнику данных и замок модуля.
   */
  readOnly?: boolean;
  /**
   * Поле обязательное — для чтения с экрана. Звёздочка у подписи рисуется псевдоэлементом и в имя поля
   * не входит (issue #1291), поэтому обязательность обязан назвать сам контрол.
   */
  required?: boolean;
  /** Показывать иконку-триггер календаря (docked date picker, issue #338). По умолчанию — да. */
  calendar?: boolean;
  /** Компактный режим (ячейка таблицы): иконка появляется по hover/focus, не занимает место постоянно. */
  compact?: boolean;
}

export function DateInput({ value, onChange, onProblem, precision = 'day', className = '', disabled = false, readOnly = false, required = false, calendar = true, compact = false }: DateInputProps) {
  /**
   * Набранное человеком — вместе со значением, от которого его начали набирать (`base`). Показанные
   * сегменты ВЫЧИСЛЯЮТСЯ (issue #858): набранное, пока значение то же, иначе — прямо из значения.
   *
   * <p>Раньше сегменты были копией значения, которую эффект переливал «когда не редактируют».
   * Копия жила своей жизнью между приходом нового значения и запуском эффекта — то есть кадр поле
   * показывало прежнюю дату.</p>
   *
   * <p>Недописанное и несуществующее при уходе из поля ОСТАЮТСЯ видимыми, с ошибкой под полем
   * (issue #1460, решение владельца): раньше они молча откатывались к прежней дате, и человек считал
   * ввод принятым. Отданная дата меняет значение — и набранное уходит само, по `base`. Значение
   * сменили снаружи (сброс формы, другая запись) — тоже: набранное относилось к прежнему.</p>
   */
  const [typed, setTyped] = useState<(Segments & { base: string }) | null>(null);
  const [focused, setFocused] = useState(false);
  const [pickerOpen, setPickerOpen] = useState(false);
  const triggerRef = useRef<HTMLButtonElement>(null);
  /** Календарь закрыт Tab'ом с края — фокус уже перенесён, возвращать его на значок не нужно. */
  const tabbedOut = useRef(false);

  const showMonth = precision !== 'year';
  const showDay = precision === 'day';

  const dayRef = useRef<HTMLInputElement>(null);
  const monRef = useRef<HTMLInputElement>(null);
  const yrRef  = useRef<HTMLInputElement>(null);
  const blurTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  /**
   * Значение, с которым человек вошёл в поле, — к нему возвращает Esc. Не текущее: сегменты отдают
   * дату, как только она складывается, и «31» поверх 10.2026 успевает отдать 31.10 по пути к 31.02
   * (поймано живой пробой окна оплаты). Запоминается при входе без набранного: вернувшись к полю с
   * ошибкой, человек правит тот же ввод, и прежним остаётся прежнее.
   */
  const startValue = useRef(value ?? '');

  const fromValue = segmentsOf(value);
  const draft = typed && typed.base === (value ?? '') ? typed : null;
  // Значение не дата («вчера», «2026/13») — показывается текстом, пока его не начали переписывать.
  const foreign = fromValue === null && draft === null;
  const shown = draft ?? fromValue ?? EMPTY_SEGMENTS;
  const { d, m, y } = shown;
  // Функциональные апдейтеры: вставка пишет все сегменты разом, и сборка от отрендеренного `shown`
  // молча потеряла бы первую правку (поймано ревью PR #863).
  const setSeg = (key: 'd' | 'm' | 'y', v: string) =>
    setTyped(prev => ({ ...(prev && prev.base === (value ?? '') ? prev : { ...shown, base: value ?? '' }), [key]: v }));
  const setD = (v: string) => setSeg('d', v);
  const setM = (v: string) => setSeg('m', v);
  const setY = (v: string) => setSeg('y', v);

  // Ошибка набранного. «Не дописана» — только после ухода из поля: посреди ввода дата и должна быть
  // недописанной. Несуществующая — сразу, как только все сегменты набраны.
  const reading = draft ? readSegments(draft, precision) : null;
  const problem = reading?.kind === 'impossible' ? SEGMENT_PROBLEMS.impossible
    : reading?.kind === 'partial' && !focused ? SEGMENT_PROBLEMS.partial
      : foreign ? SEGMENT_PROBLEMS.foreign : null;
  useEffect(() => { onProblem?.(problem); }, [onProblem, problem]);

  function onFocus() {
    clearTimeout(blurTimer.current);
    if (!focused && !draft) startValue.current = value ?? '';
    setFocused(true);
  }
  function onBlur() {
    // Delay so focus transitions between segments don't flicker focused=false. Набранное, которое
    // дало дату или пустоту, уходит вместе с фокусом; недописанное и несуществующее — остаётся с ошибкой.
    blurTimer.current = setTimeout(() => {
      setFocused(false);
      setTyped(prev => {
        const kind = prev && readSegments(prev, precision).kind;
        return kind === 'partial' || kind === 'impossible' ? prev : null;
      });
    }, 120);
  }

  // Отдаём дату, только если она есть; пустой ввод — явная очистка, остальное не отдаётся вовсе.
  function emit(dd: string, mm: string, yyyy: string) {
    const r = readSegments({ d: dd, m: mm, y: yyyy }, precision);
    if (r.kind === 'date') onChange(r.iso);
    else if (r.kind === 'empty') onChange('');
  }

  /** Вставка даты целиком — в любой сегмент (issue #1460); не дата — сегмент разберёт её сам. */
  function onPaste(e: React.ClipboardEvent) {
    if (readOnly || disabled) return;
    const got = parsePasted(e.clipboardData.getData('text'), precision);
    if (!got) return;
    e.preventDefault();
    setTyped({ ...got, base: value ?? '' });
    emit(got.d, got.m, got.y);
  }

  function advance(ref: React.RefObject<HTMLInputElement | null>) {
    ref.current?.focus();
    // Schedule select so it runs after focus
    setTimeout(() => ref.current?.select(), 0);
  }

  function handleD(raw: string) {
    const v = raw.replace(/\D/g, '').slice(0, 2);
    // Auto-advance only when filling in new digits (segment was not already full)
    if (v.length === 2 && d.length < 2) {
      setD(v); advance(monRef); emit(v, m, y);
    } else if (v.length === 1 && d === '' && +v > 3) {
      // digit 4–9 can't start a valid day — auto-pad "0X" and advance
      const p = '0' + v; setD(p); advance(monRef); emit(p, m, y);
    } else {
      setD(v); emit(v, m, y);
    }
  }

  function handleM(raw: string) {
    const v = raw.replace(/\D/g, '').slice(0, 2);
    // Auto-advance only when filling in new digits (segment was not already full)
    if (v.length === 2 && m.length < 2) {
      setM(v); advance(yrRef); emit(d, v, y);
    } else if (v.length === 1 && m === '' && +v > 1) {
      // digit 2–9 can't start a valid month — auto-pad "0X" and advance
      const p = '0' + v; setM(p); advance(yrRef); emit(d, p, y);
    } else {
      setM(v); emit(d, v, y);
    }
  }

  function handleY(raw: string) {
    const v = raw.replace(/\D/g, '').slice(0, 4);
    setY(v); emit(d, m, v);
  }

  function onDayKey(e: React.KeyboardEvent) {
    if ((e.key === '.' || e.key === '/') && d) { e.preventDefault(); advance(monRef); }
  }
  function onMonKey(e: React.KeyboardEvent) {
    if (e.key === 'Backspace' && m === '') { advance(dayRef); }
    else if ((e.key === '.' || e.key === '/') && m) { e.preventDefault(); advance(yrRef); }
  }
  function onYrKey(e: React.KeyboardEvent) {
    if (e.key === 'Backspace' && y === '') { advance(monRef); }
  }

  // Pad single digits on blur — read DOM value via ref to avoid stale closure
  // (onBlur fires synchronously before React re-renders when advance() moves focus)
  function blurD() {
    const cur = dayRef.current?.value ?? d;
    if (cur.length === 1) { const p = '0' + cur; setD(p); emit(p, m, y); }
  }
  function blurM() {
    const cur = monRef.current?.value ?? m;
    if (cur.length === 1) { const p = '0' + cur; setM(p); emit(d, p, y); }
  }
  // «18.10.26» → 2026: раньше двузначный год при уходе молча отбрасывался вместе с датой.
  function blurY() {
    const cur = yrRef.current?.value ?? y;
    if (cur.length === 2) { const p = fullYear(cur); setY(p); emit(d, m, p); }
  }

  /** Первый видимый сегмент — с него начинают переписывать значение, которое не дата. */
  const firstRef = showDay ? dayRef : showMonth ? monRef : yrRef;
  function startOver() {
    if (readOnly || disabled) return;
    setTyped({ ...EMPTY_SEGMENTS, base: value ?? '' });
    // Сегментов ещё нет — они появятся этим же рендером; фокус ставится после него.
    setTimeout(() => advance(firstRef), 0);
  }

  const seg = [
    'border-0 outline-none bg-transparent text-center min-w-0 p-0',
    disabled ? 'cursor-not-allowed' : 'focus:bg-brand-subtle',
    'rounded-sm tabular-nums',
    problem ? 'text-danger' : '',
  ].join(' ');
  const invalid = problem ? true : undefined;

  // Иконка календаря у нередактируемого поля — приглашение к действию, которого нет: открыв её,
  // человек выберет дату, и ничего не произойдёт. Убираем её вместе с жестом Alt+↓ (issue #958).
  const showCalendar = calendar && !readOnly;

  // Alt+↓ из любого сегмента открывает календарь (штатный MD3-жест).
  // Esc при правке возвращает значение, с которым вошли в поле (issue #1460), — и только его: окно,
  // где стоит поле, не закрывается (`Modal` решает о своём Esc после полей и не видит погашенного
  // здесь). Правки нет — Esc принадлежит окну.
  function onContainerKey(e: React.KeyboardEvent) {
    if (showCalendar && !disabled && e.altKey && e.key === 'ArrowDown') { e.preventDefault(); setPickerOpen(true); }
    else if (e.key === 'Escape' && !e.nativeEvent.isComposing && (draft || (value ?? '') !== startValue.current)) {
      e.preventDefault(); e.stopPropagation();
      setTyped(null);
      if ((value ?? '') !== startValue.current) onChange(startValue.current);
    }
  }

  // Значок ошибки — у поля без своей подписи (ячейка, отбор): у `DateField` ошибка стоит под рамкой.
  const problemMark = problem && !onProblem && (
    <Tooltip content={problem}>
      <span className="ml-1 shrink-0 flex items-center text-danger">
        <AlertCircle size={compact ? 13 : 14} aria-hidden />
        <span className="sr-only" role="alert">{problem}</span>
      </span>
    </Tooltip>
  );

  const segments = foreign ? (
    <div className={`flex items-center group min-w-0 ${className}`}>
      {readOnly || disabled
        ? <span className="truncate text-fg2">{value}</span>
        : <button type="button" onClick={startOver} onFocus={startOver}
            aria-label={`${value} — не дата, набрать дату заново`}
            className="truncate text-left text-fg2 rounded-sm focus:outline-none focus-visible:bg-brand-subtle">
            {value}
          </button>}
      {problemMark}
    </div>
  ) : (
    <div className={`flex items-center group ${className}`} onKeyDown={onContainerKey} onPaste={onPaste}>
      {showDay && (
        <>
          <input
            ref={dayRef} type="text" inputMode="numeric"
            placeholder="ДД" maxLength={2}
            style={{ width: '2.2ch' }}
            value={d}
            disabled={disabled} readOnly={readOnly} aria-required={required || undefined} aria-invalid={invalid}
            onChange={e => handleD(e.target.value)}
            onKeyDown={onDayKey}
            onFocus={onFocus}
            onBlur={() => { blurD(); onBlur(); }}
            className={seg}
          />
          <span className="text-fg4 select-none">.</span>
        </>
      )}
      {showMonth && (
        <>
          <input
            ref={monRef} type="text" inputMode="numeric"
            placeholder="ММ" maxLength={2}
            style={{ width: '2.2ch' }}
            value={m}
            disabled={disabled} readOnly={readOnly} aria-required={required || undefined} aria-invalid={invalid}
            onChange={e => handleM(e.target.value)}
            onKeyDown={onMonKey}
            onFocus={onFocus}
            onBlur={() => { blurM(); onBlur(); }}
            className={seg}
          />
          <span className="text-fg4 select-none">.</span>
        </>
      )}
      <input
        ref={yrRef} type="text" inputMode="numeric"
        placeholder="ГГГГ" maxLength={4}
        style={{ width: '4ch' }}
        value={y}
        disabled={disabled} readOnly={readOnly} aria-required={required || undefined} aria-invalid={invalid}
        onChange={e => handleY(e.target.value)}
        onKeyDown={onYrKey}
        onFocus={onFocus}
        onBlur={() => { blurY(); onBlur(); }}
        className={seg}
      />
      {problemMark}
      {showCalendar && (
        <Popover.Trigger asChild>
          <button ref={triggerRef} type="button" disabled={disabled} aria-label="Открыть календарь"
            className={`ml-1 shrink-0 flex items-center justify-center h-5 w-5 rounded-full text-fg4 hover:text-brand hover:bg-black/5 dark:hover:bg-white/10 transition disabled:opacity-40 focus:outline-none focus-visible:ring-2 focus-visible:ring-brand ${
              compact ? 'opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 data-[state=open]:opacity-100' : ''}`}>
            <CalendarDays size={compact ? 13 : 14} />
          </button>
        </Popover.Trigger>
      )}
    </div>
  );

  if (!showCalendar) return segments;

  // Календарю — только существующая дата: с «2026-02-31» он рисовал заголовок «undefined 2026».
  const shownDate = fromValue && readSegments(fromValue, 'day');
  const calendarValue = shownDate && shownDate.kind === 'date' ? shownDate.iso : '';

  /**
   * Tab с края календаря — дальше по форме (ревью PR #1526).
   *
   * <p>Календарь лежит в конце body, вне порядка Tab формы. Без этого Tab с его последней остановки
   * возвращался на первую кнопку календаря (проверено живым прогоном): по кругу внутри, и уйти к
   * следующему полю можно было только через Esc. Внутри календаря Tab ходит как обычно. С последней
   * остановки вперёд фокус встаёт на следующее поле после значка, с первой назад — на сам значок, и
   * календарь закрывается.</p>
   *
   * <p>Фокус переносится СРАЗУ, в обработчике клавиши. Отдать его Radix при закрытии нельзя: тот
   * возвращает фокус таймером, и до таймера фокус лежит на body — живой прогон в CI успевал увидеть
   * именно body.</p>
   */
  function onCalendarTab(e: React.KeyboardEvent<HTMLDivElement>) {
    if (e.key !== 'Tab') return;
    const stops = tabStops(e.currentTarget);
    if (document.activeElement !== (e.shiftKey ? stops[0] : stops.at(-1))) return;
    e.preventDefault();
    const trigger = triggerRef.current;
    const target = (!e.shiftKey && trigger && nextTabStop(trigger)) || trigger;
    tabbedOut.current = true;
    target?.focus();
    setPickerOpen(false);
  }
  /** Фокус уже перенесён Tab'ом — Radix не возвращает его на значок. */
  function onCalendarClosed(e: Event) {
    if (!tabbedOut.current) return;
    tabbedOut.current = false;
    e.preventDefault();
  }

  // Календарь — слой над окном, как Select и меню: портал в body (issue #1521, docs/UI.md «Слои над
  // окном»). Внутри окна его резали бы `overflow-hidden` и `transform` окна, а разворот вверх не
  // спасал бы: место Radix считает по экрану. Раньше портировали ВНУТРЬ окна (#338) — это обходило
  // дубль слоёв Radix (свои копии focus-scope и dismissable-layer у popover), которого больше нет:
  // единственную копию сторожит `scripts/radix-single-copy.test.mjs`.
  //
  // `hideWhenDetached`: поле прокрутили из видимой части тела окна — календарь прячется. Внутри окна
  // его прятал край окна; в body он висел бы над шапкой, указывая на невидимое поле.
  return (
    <Popover.Root open={pickerOpen} onOpenChange={setPickerOpen}>
      <Popover.Anchor asChild>{segments}</Popover.Anchor>
      <Popover.Portal>
        <Popover.Content align="start" sideOffset={6} collisionPadding={8} hideWhenDetached
          onKeyDown={onCalendarTab} onCloseAutoFocus={onCalendarClosed}
          onOpenAutoFocus={() => { tabbedOut.current = false; }}
          className="z-50 rounded-2xl bg-surface border border-stroke focus:outline-none"
          style={{ boxShadow: 'var(--f-shadow16)' }}>
          <Calendar value={calendarValue} precision={precision}
            onSelect={iso => onChange(iso)} onClose={() => setPickerOpen(false)} />
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}

const STOP = 'button, input, select, textarea, a[href], [tabindex]';

/** Остановки Tab внутри `root` в порядке документа: видимые, доступные, без `tabindex="-1"`. */
function tabStops(root: ParentNode): HTMLElement[] {
  return [...root.querySelectorAll<HTMLElement>(STOP)]
    .filter(el => el.tabIndex >= 0 && !el.matches(':disabled') && !el.closest('[hidden], [inert]')
      && el.getClientRects().length > 0);
}

/**
 * Следующая остановка Tab после `from` — в пределах его окна (`[role="dialog"]`: окно или поповер,
 * где стоит поле) или страницы. Только для фокуса: где рисовать календарь, это место не решает.
 */
function nextTabStop(from: HTMLElement): HTMLElement | undefined {
  const stops = tabStops(from.closest<HTMLElement>('[role="dialog"]') ?? document.body);
  const at = stops.indexOf(from);
  return at < 0 ? undefined : stops[at + 1];
}
