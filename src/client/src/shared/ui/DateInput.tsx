import { Fragment, useEffect, useId, useMemo, useRef, useState } from 'react';
import * as Popover from '@radix-ui/react-popover';
import { AlertCircle, CalendarDays } from 'lucide-react';
import type { DatePrecision } from '@/shared/api/types';
import { Calendar } from './Calendar';
import { Tooltip } from './Tooltip';
import { EMPTY_SEGMENTS, SEGMENT_PROBLEMS, fullYear, parsePasted, readSegments, segmentsOf, type Segments } from './dateSegments';
import { dateMask, type DatePart } from '@/shared/format/dateMask';
import { companyToday } from '@/shared/format/companyDay';
import { daysInMonth, MIN_YEAR, MAX_YEAR } from '@/shared/utils/date';
import { resolveLocale, useLocale } from '@/shared/hooks/useLocale';

interface DateInputProps {
  /**
   * Имя поля для чтения с экрана — у группы сегментов (issue #1463): «Дата платежа». Обязательное:
   * без него скринридер слышал три безымянных «день, месяц, год», а живые прогоны искали поле по
   * подсказке «ДД», которой у en-US нет. Видимую подпись рисует вызывающий, это — её же текст.
   */
  label: string;
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
  /**
   * Точность ввода: 'day' (по умолч.), 'month', 'year'. Маска — по языку пользователя (issue #1461):
   * ДД.ММ.ГГГГ у ru-RU, MM/DD/YYYY у en-US; точность убирает лишние сегменты (ММ.ГГГГ, MM/YYYY).
   */
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

export function DateInput({ label, value, onChange, onProblem, precision = 'day', className = '', disabled = false, readOnly = false, required = false, calendar = true, compact = false }: DateInputProps) {
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

  // Порядок сегментов, разделитель и обозначения — языка пользователя (issue #1461). Хранимое значение
  // от языка не зависит. Полный порядок нужен вставке: дату целиком вставляют и в поле месяца.
  const [storedLocale] = useLocale();
  const { mask, fullOrder } = useMemo(() => {
    const locale = resolveLocale(storedLocale);
    return { mask: dateMask(locale, precision), fullOrder: dateMask(locale, 'day').order };
  }, [storedLocale, precision]);

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
      : foreign && !readOnly && !disabled ? SEGMENT_PROBLEMS.foreign : null;
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
    const got = parsePasted(e.clipboardData.getData('text'), precision, fullOrder);
    if (!got) return;
    e.preventDefault();
    setTyped({ ...got, base: value ?? '' });
    emit(got.d, got.m, got.y);
  }

  /**
   * Выделение — сразу за фокусом: следующая цифра заменит сегмент. Таймер только повторяет его, если в
   * сегмент ещё не набрали: на медленной машине он срабатывал после первой цифры и выделял её, и
   * «3102» поверх 19.10 давало месяц «2» (живые прогоны PR #1544).
   */
  function advance(ref: React.RefObject<HTMLInputElement | null>) {
    const el = ref.current;
    if (!el) return;
    el.focus();
    el.select();
    const was = el.value;
    setTimeout(() => { if (document.activeElement === el && el.value === was) el.select(); }, 0);
  }

  const refs: Record<DatePart, React.RefObject<HTMLInputElement | null>> = { d: dayRef, m: monRef, y: yrRef };
  /** Сосед сегмента в порядке языка: у en-US за месяцем идёт день, у ru-RU — год. */
  function go(part: DatePart, step: 1 | -1) {
    const next = mask.order[mask.order.indexOf(part) + step];
    if (next) advance(refs[next]);
  }

  function handleD(raw: string) {
    const v = raw.replace(/\D/g, '').slice(0, 2);
    // Auto-advance only when filling in new digits (segment was not already full)
    if (v.length === 2 && d.length < 2) {
      setD(v); go('d', 1); emit(v, m, y);
    } else if (v.length === 1 && d === '' && +v > 3) {
      // digit 4–9 can't start a valid day — auto-pad "0X" and advance
      const p = '0' + v; setD(p); go('d', 1); emit(p, m, y);
    } else {
      setD(v); emit(v, m, y);
    }
  }

  function handleM(raw: string) {
    const v = raw.replace(/\D/g, '').slice(0, 2);
    // Auto-advance only when filling in new digits (segment was not already full)
    if (v.length === 2 && m.length < 2) {
      setM(v); go('m', 1); emit(d, v, y);
    } else if (v.length === 1 && m === '' && +v > 1) {
      // digit 2–9 can't start a valid month — auto-pad "0X" and advance
      const p = '0' + v; setM(p); go('m', 1); emit(d, p, y);
    } else {
      setM(v); emit(d, v, y);
    }
  }

  function handleY(raw: string) {
    const v = raw.replace(/\D/g, '').slice(0, 4);
    // Год не последний бывает у языка с порядком «год, месяц, день» — тогда дальше идёт месяц.
    if (v.length === 4 && y.length < 4) go('y', 1);
    setY(v); emit(d, m, v);
  }

  /**
   * ↑/↓ — значение сегмента на шаг, как у нативного поля даты (issue #1463): день и месяц по кругу,
   * пустой сегмент — с края (↑ — 1, ↓ — последний), пустой год — год компании. День ходит по кругу
   * своего месяца, если месяц набран: иначе ↑ с 30.11 давал бы 31.11 — «такой даты нет». Год — в
   * границах календаря; недописанный (1 или 3 цифры) стрелка не трогает, чтобы не стереть набранное.
   * Значение выделяется снова: следующая стрелка или цифра работает со всем сегментом.
   */
  function step(part: DatePart, delta: 1 | -1, input: HTMLInputElement) {
    const cur = shown[part];
    let next: string;
    if (part === 'y') {
      if (cur.length === 1 || cur.length === 3) return;
      const year = cur === '' ? +companyToday().slice(0, 4) : +(cur.length === 2 ? fullYear(cur) : cur) + delta;
      next = String(Math.min(MAX_YEAR, Math.max(MIN_YEAR, year)));
    } else {
      const top = part === 'm' ? 12 : dayTop(shown);
      const n = cur === '' || +cur > top ? (delta > 0 ? 1 : top) : ((+cur - 1 + delta + top) % top) + 1;
      next = String(n).padStart(2, '0');
    }
    const segs = { ...shown, [part]: next };
    setSeg(part, next);
    emit(segs.d, segs.m, segs.y);
    setTimeout(() => { if (document.activeElement === input) input.select(); }, 0);
  }

  /**
   * Разделитель переводит к следующему сегменту, Backspace в пустом — к предыдущему, ←/→ с края — к
   * соседнему (issue #1463). Цифра в полный сегмент без выделения заменяет его, как в нативном поле
   * даты: иначе её молча съедал `maxLength`. Alt+↓ — не шаг, а календарь (`onContainerKey`).
   */
  const onSegmentKey = (part: DatePart) => (e: React.KeyboardEvent<HTMLInputElement>) => {
    const cur = shown[part];
    const input = e.currentTarget;
    if (e.key === 'Backspace' && cur === '') go(part, -1);
    else if (SEPARATORS.includes(e.key) && cur) { e.preventDefault(); go(part, 1); }
    else if (/^\d$/.test(e.key) && input.value.length >= input.maxLength && input.selectionStart === input.selectionEnd) input.select();
    else if ((e.key === 'ArrowUp' || e.key === 'ArrowDown') && !e.altKey && !readOnly && !disabled) {
      e.preventDefault(); step(part, e.key === 'ArrowUp' ? 1 : -1, input);
    }
    else if (e.key === 'ArrowLeft' && atEdge(input, 'start')) { e.preventDefault(); go(part, -1); }
    else if (e.key === 'ArrowRight' && atEdge(input, 'end')) { e.preventDefault(); go(part, 1); }
  };

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

  const isSegment = (t: EventTarget) => t === dayRef.current || t === monRef.current || t === yrRef.current;

  /** Первый видимый сегмент — с него начинают переписывать значение, которое не дата. */
  const firstRef = refs[mask.order[0]];
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
  // Ошибка для скринридера — описанием сегментов (`aria-describedby`), и у `DateField` тоже: его
  // подпись под рамкой с полем не связана. Живая область объявляет только СМЕНУ ошибки: `role="alert"`
  // объявлял бы каждую ошибку при отрисовке, и таблица с двадцатью ячейками «не дата» зачитывалась бы
  // подряд (ревью PR #1542).
  const problemId = useId();
  const describedBy = problem ? problemId : undefined;

  // Иконка календаря у нередактируемого поля — приглашение к действию, которого нет: открыв её,
  // человек выберет дату, и ничего не произойдёт. Убираем её вместе с жестом Alt+↓ (issue #958).
  const showCalendar = calendar && !readOnly;

  // Alt+↓ из любого сегмента открывает календарь (штатный MD3-жест).
  // Esc при правке возвращает значение, с которым вошли в поле (issue #1460), — и только его: окно,
  // где стоит поле, не закрывается (`Modal` решает о своём Esc после полей и не видит погашенного
  // здесь). Правки нет — Esc принадлежит окну. Только из сегмента: на значке календаря `startValue`
  // не снят (фокус туда приходит мимо `onFocus` сегментов) и мог устареть с монтирования — откат к нему
  // стёр бы дату, пришедшую с данными формы (ревью PR #1542).
  function onContainerKey(e: React.KeyboardEvent) {
    if (showCalendar && !disabled && e.altKey && e.key === 'ArrowDown') { e.preventDefault(); setPickerOpen(true); }
    else if (e.key === 'Escape' && !e.nativeEvent.isComposing && isSegment(e.target)
      && (draft || (value ?? '') !== startValue.current)) {
      e.preventDefault(); e.stopPropagation();
      setTyped(null);
      if ((value ?? '') !== startValue.current) onChange(startValue.current);
    }
  }

  // Значок ошибки — у поля без своей подписи (ячейка, отбор): у `DateField` ошибка стоит под рамкой.
  const problemMark = (
    <>
      <span id={problemId} className="sr-only" aria-live="polite">{problem ?? ''}</span>
      {problem && !onProblem && (
        <Tooltip content={problem}>
          <span className="ml-1 shrink-0 flex items-center text-danger">
            <AlertCircle size={compact ? 13 : 14} aria-hidden />
          </span>
        </Tooltip>
      )}
    </>
  );

  const SEGMENTS: Record<DatePart, { ref: React.RefObject<HTMLInputElement | null>; change: (raw: string) => void; blur?: () => void; len: number }> = {
    d: { ref: dayRef, change: handleD, blur: blurD, len: 2 },
    m: { ref: monRef, change: handleM, blur: blurM, len: 2 },
    y: { ref: yrRef, change: handleY, blur: blurY, len: 4 },
  };

  const segments = foreign ? (
    <div role="group" aria-label={label} className={`flex items-center group min-w-0 ${className}`}>
      {readOnly || disabled
        ? <span className="truncate text-fg2">{value}</span>
        // Ввод начинается щелчком, Enter или пробелом, а не фокусом: проход Tab'ом по форме не должен
        // стирать показанное и уводить фокус в сегменты (ревью PR #1542).
        : <button type="button" onClick={startOver} aria-describedby={describedBy}
            aria-label={`${value} — не дата, набрать дату заново`}
            className="truncate text-left text-fg2 rounded-sm focus:outline-none focus-visible:bg-brand-subtle">
            {value}
          </button>}
      {problemMark}
    </div>
  ) : (
    <div role="group" aria-label={label} className={`flex items-center group ${className}`} onKeyDown={onContainerKey} onPaste={onPaste}>
      {mask.order.map((part, i) => {
        const { ref, change, blur, len } = SEGMENTS[part];
        return (
          <Fragment key={part}>
            {i > 0 && <span className="text-fg4 select-none">{mask.sep}</span>}
            {/* Ширина — по образцам (см. `SIZER`): обозначение и цифры. */}
            <span className="inline-grid">
              <span data-sizer={mask.labels[part]} aria-hidden className={SIZER} />
              <span data-sizer={len === 4 ? '0000' : '00'} aria-hidden className={SIZER} />
              <input
                ref={ref} type="text" inputMode="numeric"
                placeholder={mask.labels[part]} aria-label={mask.names[part]} maxLength={len}
                value={shown[part]}
                disabled={disabled} readOnly={readOnly} aria-required={required || undefined} aria-invalid={invalid} aria-describedby={describedBy}
                onChange={e => change(e.target.value)}
                onKeyDown={onSegmentKey(part)}
                onFocus={onFocus}
                onBlur={() => { blur?.(); onBlur(); }}
                className={`${seg} col-start-1 row-start-1 w-0 min-w-full`}
              />
            </span>
          </Fragment>
        );
      })}
      {problemMark}
      {showCalendar && (
        <Popover.Trigger asChild>
          <button ref={triggerRef} type="button" disabled={disabled} aria-label="Открыть календарь"
            className={`-my-1.5 shrink-0 flex items-center justify-center h-8 w-8 rounded-full text-fg4 hover:text-brand hover:bg-black/5 dark:hover:bg-white/10 transition disabled:opacity-40 focus:outline-none focus-visible:ring-2 focus-visible:ring-brand ${
              compact ? `${COMPACT_TRIGGER} opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 data-[state=open]:opacity-100` : 'ml-0.5'}`}>
            <CalendarDays size={compact ? 14 : 16} />
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
            // Выбранный день снимает набранное: совпади он с текущим значением, значение не сменится,
            // и ошибка набранного осталась бы над выбранной датой (ревью PR #1542).
            onSelect={iso => { setTyped(null); onChange(iso); }} onClose={() => setPickerOpen(false)} />
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}

/**
 * Невидимый образец, растягивающий ячейку сегмента: ширина по содержимому, а не в `ch` — «MM» и «YYYY»
 * шире цифр, и у en-US подсказка обрезалась (проба стенда, PR #1544). Текст образца — в псевдоэлементе,
 * мимо `textContent` и скринридера. Поле в ячейке — `w-0 min-w-full`: своей ширины по умолчанию
 * (около 20 знаков) оно в ячейку не вносит.
 */
const SIZER = 'invisible col-start-1 row-start-1 px-0.5 tabular-nums after:content-[attr(data-sizer)]';
/**
 * Кнопка календаря в ячейке: нажимать — те же 32 px, а места в строке на 6 px меньше (ревью PR #1548):
 * зона нажатия заходит на правое поле ячейки (`px-1.5`), а не на цифры года слева. Ширину колонки под
 * поле — 140 px — держит `DEFAULT_COL_WIDTHS`.
 */
const COMPACT_TRIGGER = '-mr-1.5';
const STOP = 'button, input, select, textarea, a[href], [tabindex]';
/** Клавиши-разделители: набрав «18.» или «10/», человек ждёт следующий сегмент при любом языке. */
const SEPARATORS = ['.', '/', '-'];

/**
 * Последний день для стрелок: дней в набранном месяце; год не дописан — по високосному (29 февраля
 * не отсекается раньше, чем год известен); месяца нет — 31.
 */
function dayTop({ m, y }: Segments): number {
  const month = +m;
  if (!month || month > 12) return 31;
  return daysInMonth(y.length === 4 ? +y : 2000, month);
}

/** Курсор у края сегмента или выделен весь сегмент — стрелка уводит к соседнему. */
function atEdge(input: HTMLInputElement, side: 'start' | 'end'): boolean {
  const { selectionStart: from, selectionEnd: to, value } = input;
  if (from === 0 && to === value.length) return true;
  return side === 'start' ? to === 0 : from === value.length;
}

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
