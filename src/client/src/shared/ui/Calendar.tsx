import { useState, useRef, useLayoutEffect, useMemo } from 'react';
import { Icon } from './Icon';
import { ArrowDropDown, ArrowDropUp, Check, ChevronLeft, ChevronRight } from './icons';
import type { DatePrecision } from '@/shared/api/types';
import { parseISO, toISO, daysInMonth, monthMatrix, dayInWeek, outOfRange, truncISO, MIN_YEAR, MAX_YEAR } from '@/shared/utils/date';
import { firstDayOfWeek, monthNames, spokenDate, weekdayNames } from '@/shared/format/dateMask';
import { companyToday } from '@/shared/format/companyDay';
import { resolveLocale, useLocale } from '@/shared/hooks/useLocale';
import { Button, IconButton } from './Button';
import { BASE, HEIGHT, LABEL, padding } from './buttonClasses';

/**
 * Docked date picker MD3 ([UI-80], макет А md3-16; начало — issue #338): чистая управляемая поверхность
 * выбора даты. Терминальный вид — по точности (#60): day → дни месяца, month → список месяцев под
 * селектором года, year → список лет. Названия месяцев и дней недели и первый день недели — по языку
 * пользователя (issue #1461), не таблицей. Аддитивен к сегментному вводу — сюда приходит текущее value,
 * отсюда уходит выбранный полный ISO.
 *
 * Шапка дней — два селектора `‹ месяц ▾ ›` `‹ год ▾ ›`: стрелки листают месяц и год, кнопка открывает
 * список на месте сетки. Выбор в списке, открытом из шапки, возвращает к прежнему виду и даты не ставит;
 * Esc в нём — тоже назад (поле не даёт Radix закрыть слой: признак `data-calendar-back` у корня).
 *
 * «Сегодня» — день компании, а не браузера (issue #1463): кольцо в сетке и месяц, с которого открывается
 * пустое поле, — по `companyToday`. Дни — APG grid: строки `row`, клетки `gridcell` с `aria-selected`,
 * строка заголовков `columnheader`; месяцы и годы — listbox, ↑ ↓ по одному, Home / End, Enter.
 *
 * Внизу — строка действий (issue #1463, вариант В): «Очистить» слева, «Сегодня» справа. Выбор календарь
 * не подтверждает — щелчок по дню сразу ставит дату, — поэтому «Отмена / OK» из MD3 здесь нет
 * (отступление, docs/UI.md).
 *
 * Границы min/max (issue #1464): дни, месяцы и годы вне них неактивны, фокус и стрелки шапки за них не
 * заходят — у неактивной кнопки фокуса нет, и роллинг-фокус молча потерялся бы. Сравнение — по точности
 * (`outOfRange`). «Сегодня» вне границ скрыта, а не ставит другой день; пустое поле открывается на
 * ближайшей границе. `range` — лента периода у `DateRange`: от «с» до «по».
 */
type View = 'days' | 'months' | 'years';

/** «Сегодня» по точности поля, а не по открытому виду: иначе кнопка обещала бы день, а ставила 01. */
const TODAY_LABEL: Record<DatePrecision, string> = { day: 'Сегодня', month: 'Этот месяц', year: 'Этот год' };

export function Calendar({ value, onSelect, precision = 'day', onClose, onClear, min, max, range }: {
  /** Текущее значение — полный ISO YYYY-MM-DD или ''. */
  value: string;
  /** Выбор завершён → полный ISO (скрытые точностью части = 01, как в сегментном вводе). */
  onSelect: (iso: string) => void;
  precision?: DatePrecision;
  /** Закрыть поповер (выбор в терминальном виде). */
  onClose?: () => void;
  /**
   * «Очистить» в строке действий; не передан — кнопки нет. Можно ли стирать, решает поле, а не
   * календарь: обязательность и «не дату» знает оно.
   */
  onClear?: () => void;
  /** Самая ранняя и самая поздняя допустимые даты — полный ISO; нет — не ограничено. */
  min?: string;
  max?: string;
  /** Период, частью которого служит значение: обе границы — полный ISO. Рисуется лентой между ними. */
  range?: [string, string];
}) {
  const todayISO = companyToday();
  const today = parseISO(todayISO)!;
  const sel = parseISO(value);
  const [stored] = useLocale();
  const locale = resolveLocale(stored);
  const { months, monthsShort, weekdays, firstDay } = useMemo(() => ({
    months: monthNames(locale, 'long'), monthsShort: monthNames(locale, 'short'),
    weekdays: weekdayNames(locale), firstDay: firstDayOfWeek(locale),
  }), [locale]);
  // Фокус начинается на значении или сегодня, но в границах: на неактивной ячейке он не встанет.
  const into = (iso: string) => min && iso < min ? min.slice(0, 10) : max && iso > max ? max.slice(0, 10) : iso;
  const start = parseISO(into(sel ? toISO(sel.y, sel.m, sel.d) : todayISO))!;
  const minY = min ? +min.slice(0, 4) : MIN_YEAR, maxY = max ? +max.slice(0, 4) : MAX_YEAR;
  const clampYear = (y: number) => Math.min(maxY, MAX_YEAR, Math.max(minY, MIN_YEAR, y));

  // Терминальный вид точности: year → только годы; month → месяцы; day → дни.
  const terminalView: View = precision === 'year' ? 'years' : precision === 'month' ? 'months' : 'days';
  const [view, setView] = useState<View>(terminalView);
  /** Вид, к которому вернёт выбор или Esc в списке, открытом из шапки. */
  const back: View = view === 'years' && precision === 'month' ? 'months' : 'days';
  const fromHeader = view !== terminalView;
  // Клавиатурный фокус (роллинг). d актуален в day-view; m — в day/month; y — везде.
  const [fy, setFy] = useState(clampYear(start.y));
  const [fm, setFm] = useState(start.m);
  const [fd, setFd] = useState(start.d);

  const gridRef = useRef<HTMLDivElement>(null);
  const shownView = useRef<View | null>(null);
  // После смены фокуса/вида — переводим DOM-фокус на активную ячейку (роллинг tabindex).
  useLayoutEffect(() => {
    const box = gridRef.current, el = box?.querySelector<HTMLElement>('[data-focus="true"]');
    const opened = shownView.current !== view;
    shownView.current = view;
    if (!box || !el) return;
    if (view === 'days') { el.focus(); return; }
    // Список прокручивается сам и только сам: фокус с прокруткой (и `scrollIntoView`) сдвинул бы и слой
    // календаря, у которого своя прокрутка по месту, — шапка уезжала за край. Открыт — отмеченная строка
    // посередине, над ней видно соседей; дальше — ровно настолько, чтобы строка фокуса была видна.
    el.focus({ preventScroll: true });
    const top = el.offsetTop, bottom = top + el.offsetHeight;
    if (opened) box.scrollTop = top - (box.clientHeight - el.offsetHeight) / 2;
    else if (top < box.scrollTop) box.scrollTop = top;
    else if (bottom > box.scrollTop + box.clientHeight) box.scrollTop = bottom - box.clientHeight;
  }, [fy, fm, fd, view]);

  function selectDay(y: number, m: number, d: number) { onSelect(toISO(y, m, d)); onClose?.(); }
  function pickMonth(m: number) {
    if (precision === 'month') { onSelect(toISO(fy, m, 1)); onClose?.(); return; }
    focusOn(fy, m, Math.min(fd, daysInMonth(fy, m)));
    setView('days');
  }
  function pickYear(y: number) {
    if (precision === 'year') { onSelect(toISO(y, 1, 1)); onClose?.(); return; }
    // Год выбран → назад; день и месяц фокуса — в границах нового года.
    focusOn(y, fm, Math.min(fd, daysInMonth(y, fm)), precision);
    setView(back);
  }

  /** Перевести фокус на дату, придвинув её к границе min/max (по точности вида), если она за ней. */
  function focusOn(y: number, m: number, d: number, level: DatePrecision = 'day') {
    let iso = toISO(clampYear(y), m, d);
    if (min && iso < truncISO(min, level)) iso = min.slice(0, 10);
    else if (max && truncISO(iso, level) > truncISO(max, level)) iso = max.slice(0, 10);
    const p = parseISO(iso)!;
    setFy(p.y); setFm(p.m); setFd(p.d);
  }

  // ── day-view: перемещение фокуса по дням (через нативный Date — корректный роллинг) ──
  function shiftDay(delta: number) {
    const dt = new Date(fy, fm - 1, fd); dt.setDate(dt.getDate() + delta);
    focusOn(dt.getFullYear(), dt.getMonth() + 1, dt.getDate());
  }
  function shiftMonth(delta: number) {
    const dt = new Date(fy, fm - 1 + delta, 1);
    const y = dt.getFullYear(), m = dt.getMonth() + 1;
    focusOn(y, m, Math.min(fd, daysInMonth(y, m)));
  }

  /** Номер дня в фокусе внутри недели языка: 0 — её первый день. */
  const dayOfWeek = () => dayInWeek(new Date(fy, fm - 1, fd), firstDay);

  function onDaysKey(e: React.KeyboardEvent) {
    switch (e.key) {
      case 'ArrowLeft': shiftDay(-1); break;
      case 'ArrowRight': shiftDay(1); break;
      case 'ArrowUp': shiftDay(-7); break;
      case 'ArrowDown': shiftDay(7); break;
      case 'Home': shiftDay(-dayOfWeek()); break;    // к первому дню недели языка
      case 'End': shiftDay(6 - dayOfWeek()); break;  // к последнему
      case 'PageUp': shiftMonth(e.shiftKey ? -12 : -1); break;
      case 'PageDown': shiftMonth(e.shiftKey ? 12 : 1); break;
      case 'Enter': case ' ': selectDay(fy, fm, fd); break;
      default: return;
    }
    e.preventDefault();
  }

  /** Клавиши списка: ↑ ↓ по одному, Home / End — к краю, PageUp / PageDown у месяцев — год. */
  function onMonthsKey(e: React.KeyboardEvent) {
    let m = fm, y = fy;
    switch (e.key) {
      case 'ArrowUp': m = Math.max(1, m - 1); break;
      case 'ArrowDown': m = Math.min(12, m + 1); break;
      case 'Home': m = 1; break;
      case 'End': m = 12; break;
      case 'PageUp': y = clampYear(y - 1); break;
      case 'PageDown': y = clampYear(y + 1); break;
      case 'Enter': case ' ': pickMonth(fm); e.preventDefault(); return;
      default: return;
    }
    focusOn(y, m, 1, 'month'); e.preventDefault();
  }
  function onYearsKey(e: React.KeyboardEvent) {
    switch (e.key) {
      case 'ArrowUp': setFy(y => clampYear(y - 1)); break;
      case 'ArrowDown': setFy(y => clampYear(y + 1)); break;
      case 'Home': setFy(clampYear(MIN_YEAR)); break;
      case 'End': setFy(clampYear(MAX_YEAR)); break;
      case 'Enter': case ' ': pickYear(fy); break;
      default: return;
    }
    e.preventDefault();
  }

  /** Esc в списке, открытом из шапки, — назад, а не закрыть: календарь закроет следующий Esc. */
  function onRootKey(e: React.KeyboardEvent) {
    if (e.key !== 'Escape' || !fromHeader) return;
    e.preventDefault();
    setView(back);
  }

  /** Лента периода: место клетки в ней по точности вида — начало, середина, конец или вне. */
  // Концы ленты усечены по точности вида один раз на рисунок, а не в каждой клетке.
  const [rFrom, rTo] = range ?? ['', ''];
  const ends = (level: DatePrecision) => rFrom && rTo ? [truncISO(rFrom, level), truncISO(rTo, level)] as const : null;
  const bandEnds = { day: ends('day'), month: ends('month'), year: ends('year') };
  const band = (iso: string, level: DatePrecision): 'start' | 'mid' | 'end' | null => {
    const e = bandEnds[level];
    if (!e) return null;
    const [a, b] = e, v = truncISO(iso, level);
    if (a >= b || v < a || v > b) return null;
    return v === a ? 'start' : v === b ? 'end' : 'mid';
  };

  // ── Шапка: селекторы месяца и года ──
  // Стрелка неактивна, если соседний месяц или год целиком за границей.
  const monthPrevOff = !!min && toISO(fy, fm, 1) <= min;
  const monthNextOff = !!max && toISO(fy, fm, daysInMonth(fy, fm)) >= max;
  const yearPrevOff = fy <= minY, yearNextOff = fy >= maxY;
  const prevYear = () => view === 'months' ? focusOn(fy - 1, fm, 1, 'month') : shiftMonth(-12);
  const nextYear = () => view === 'months' ? focusOn(fy + 1, fm, 1, 'month') : shiftMonth(12);
  const toggle = (list: View) => setView(view === list ? back : list);
  const header = (
    <div className="flex items-center h-14">
      {precision === 'day' && (
        <Selector open={view === 'months'} hidden={view === 'years'} label={monthsShort[fm - 1]}
          name={`${months[fm - 1]} ${fy}, выбрать месяц`} onToggle={() => toggle('months')}
          prev={{ label: 'Предыдущий месяц', off: monthPrevOff, go: () => shiftMonth(-1) }}
          next={{ label: 'Следующий месяц', off: monthNextOff, go: () => shiftMonth(1) }} />
      )}
      <span className="flex-1" />
      {precision === 'year'
        ? <span className="px-3 text-label-large text-on-surface-variant tabular-nums">{fy}</span>
        : <Selector open={view === 'years'} hidden={view === 'months' && fromHeader} label={String(fy)}
            name={`${fy}, выбрать год`} onToggle={() => toggle('years')}
            prev={{ label: 'Предыдущий год', off: yearPrevOff, go: prevYear }}
            next={{ label: 'Следующий год', off: yearNextOff, go: nextYear }} />}
    </div>
  );

  // ── Тело по виду ──
  let body: React.ReactNode;
  if (view === 'days') {
    const weeks = monthMatrix(fy, fm, firstDay);
    body = (
      <div ref={gridRef} role="grid" aria-label="Календарь" onKeyDown={onDaysKey} className="min-h-82">
        <div role="row" className="grid grid-cols-7">
          {weekdays.map(w => <div key={w} role="columnheader" className="h-10 flex items-center justify-center text-body-large text-on-surface">{w}</div>)}
        </div>
        {weeks.map((week, wi) => (
          <div key={wi} role="row" className="grid grid-cols-7">
            {week.map((d, di) => {
              if (d == null) return <div key={di} role="gridcell" className="h-12" />;
              const iso = toISO(fy, fm, d);
              const isSel = !!sel && sel.y === fy && sel.m === fm && sel.d === d;
              const isToday = iso === todayISO;
              const place = band(iso, 'day');
              const filled = isSel || place === 'start' || place === 'end';
              return (
                <div key={di} role="gridcell" aria-selected={isSel} className="h-12 flex items-center justify-center relative">
                  {place && <span aria-hidden className={`absolute inset-y-1 bg-secondary-container ${BAND[place]}`} />}
                  <button type="button" data-focus={d === fd} tabIndex={d === fd ? 0 : -1}
                    aria-current={isToday ? 'date' : undefined} disabled={outOfRange(iso, min, max)}
                    onClick={() => selectDay(fy, fm, d)}
                    // Фокус роллинга виден ВСЕГДА (issue #338): открыли мышью — фокус на клетке есть, а
                    // :focus-visible молчит, и курсора не было бы. Кольцо общее ([UI-64]).
                    className={`relative size-10 flex items-center justify-center rounded-full text-body-large state-layer disabled-content disabled:pointer-events-none ${
                      d === fd ? 'focus-ring-mark' : ''} ${
                      filled ? 'bg-primary text-on-primary'
                      : isToday ? 'inset-ring inset-ring-primary text-primary'
                      : place === 'mid' ? 'text-on-secondary-container' : 'text-on-surface'}`}>
                    {d}
                  </button>
                </div>
              );
            })}
          </div>
        ))}
      </div>
    );
  } else if (view === 'months') {
    body = (
      <List ref={gridRef} label="Выбор месяца" onKeyDown={onMonthsKey}>
        {months.map((name, i) => {
          const m = i + 1;
          // Из шапки отмечен показанный месяц, у точности «месяц» — значение и концы периода.
          const place = precision === 'month' ? band(toISO(fy, m, 1), 'month') : null;
          const checked = fromHeader ? m === fm : (!!sel && sel.y === fy && sel.m === m) || place === 'start' || place === 'end';
          return (
            <Option key={m} focus={m === fm} checked={checked} band={!!place}
              current={today.y === fy && today.m === m} off={outOfRange(toISO(fy, m, 1), min, max, 'month')}
              onPick={() => pickMonth(m)}>
              {name}
            </Option>
          );
        })}
      </List>
    );
  } else {
    const years = Array.from({ length: MAX_YEAR - MIN_YEAR + 1 }, (_, i) => MIN_YEAR + i);
    body = (
      <List ref={gridRef} label="Выбор года" onKeyDown={onYearsKey}>
        {years.map(y => {
          const place = precision === 'year' ? band(toISO(y, 1, 1), 'year') : null;
          const checked = fromHeader ? y === fy : (!!sel && sel.y === y) || place === 'start' || place === 'end';
          return (
            <Option key={y} focus={y === fy} checked={checked} band={!!place} current={y === today.y}
              off={outOfRange(toISO(y, 1, 1), min, max, 'year')} onPick={() => pickYear(y)}>
              <span className="tabular-nums">{y}</span>
            </Option>
          );
        })}
      </List>
    );
  }

  // «Сегодня» — день компании, скрытые точностью части — 01, как у выбора. Имя для чтения с экрана
  // называет дату: около полуночи день компании расходится с часами компьютера, и голое «Сегодня» не
  // говорит, какой день встанет. Видимой подписи хватает слова — день показывает кольцо в сетке.
  const todayLabel = TODAY_LABEL[precision];
  const todayPick = precision === 'year' ? toISO(today.y, 1, 1) : precision === 'month' ? toISO(today.y, today.m, 1) : todayISO;
  // Сегодня за границей — кнопки нет: «Сегодня», ставящая другой день, обещала бы не то (#1464).
  const showToday = !outOfRange(todayPick, min, max, precision);

  // 360 и не шире экрана без 8 по краям: место даёт слой (`DateInput`), колонки дней — доли.
  return (
    <div className="w-90 max-w-full px-3 pb-1 select-none text-on-surface" onKeyDown={onRootKey}
      data-calendar-back={fromHeader || undefined}>
      {header}
      {body}
      {/* Строка действий — без черты над ней ([UI-80]). */}
      {(onClear || showToday) && (
        <div data-testid="calendar-actions" className="flex items-center h-13">
          {onClear && <Button variant="text" onClick={onClear}>Очистить</Button>}
          {showToday && (
            <Button variant="text" className="ml-auto" aria-label={`${todayLabel}, ${spokenDate(todayISO, locale, precision)}`}
              onClick={() => { onSelect(todayPick); onClose?.(); }}>
              {todayLabel}
            </Button>
          )}
        </div>
      )}
    </div>
  );
}

/** Полоса ленты периода под днём: у начала — правая половина, у конца — левая. */
const BAND = { start: 'left-1/2 right-0', mid: 'inset-x-0', end: 'left-0 right-1/2' } as const;

type Arrow = { label: string; off: boolean; go: () => void };

/**
 * Селектор шапки `‹ подпись ▾ ›`: стрелки — кнопки-иконки 40, подпись — текстовая кнопка цветом
 * on-surface-variant, открытая — на secondary-container со стрелкой вверх. Пока список открыт, стрелок
 * нет (их место держит пустота — подпись не сдвигается), а селектор другого списка не рисуется вовсе.
 */
function Selector({ open, hidden, label, name, onToggle, prev, next }: {
  open: boolean; hidden: boolean; label: string; name: string; onToggle: () => void; prev: Arrow; next: Arrow;
}) {
  if (hidden) return null;
  const arrow = (a: Arrow, icon: typeof ChevronLeft) => open
    ? <span aria-hidden className="size-10 shrink-0" />
    : <IconButton label={a.label} icon={icon} disabled={a.off} onClick={a.go} />;
  return (
    <div className="flex items-center">
      {arrow(prev, ChevronLeft)}
      <button type="button" aria-label={name} aria-expanded={open} onClick={onToggle}
        className={`${BASE} touch-target rounded-full ${HEIGHT} ${LABEL} ${padding('text', { trailing: true })} gap-1 ${
          open ? 'bg-secondary-container text-on-secondary-container' : 'text-on-surface-variant'}`}>
        {label}
        <Icon icon={open ? ArrowDropUp : ArrowDropDown} size={18} />
      </button>
      {arrow(next, ChevronRight)}
    </div>
  );
}

/**
 * Список месяцев или лет на месте сетки дней: та же высота (дни недели 40 + 6 × 48), прокрутка внутри,
 * черта над ним — край меню docked date picker. Во всю ширину календаря, мимо его полей 12.
 */
function List({ ref, label, onKeyDown, children }: {
  ref: React.Ref<HTMLDivElement>; label: string; onKeyDown: (e: React.KeyboardEvent) => void; children: React.ReactNode;
}) {
  return (
    <div ref={ref} role="listbox" aria-label={label} onKeyDown={onKeyDown}
      // `relative` — точка отсчёта `offsetTop` строк: по ней список прокручивает себя.
      className="relative -mx-3 h-82 overflow-y-auto border-t border-outline-variant">
      {children}
    </div>
  );
}

/**
 * Строка списка — 48, галочка у отмеченного, отмеченное и период — на secondary-container. Неактивная за
 * границей min/max: фокус и выбор её обходят.
 */
function Option({ focus, checked, band, current, off, onPick, children }: {
  focus: boolean; checked: boolean; band: boolean; current: boolean; off: boolean; onPick: () => void; children: React.ReactNode;
}) {
  return (
    <div role="option" aria-selected={checked} aria-disabled={off || undefined} aria-current={current ? 'date' : undefined}
      data-focus={focus} tabIndex={focus ? 0 : -1} onClick={off ? undefined : onPick}
      className={`flex items-center gap-4 h-12 px-4 text-body-large cursor-default state-layer focus-ring-inset disabled-content ${
        off ? 'pointer-events-none' : ''} ${
        checked || band ? 'bg-secondary-container text-on-secondary-container' : 'text-on-surface'}`}>
      {checked ? <Icon icon={Check} size={24} className="shrink-0" /> : <span aria-hidden className="size-6 shrink-0" />}
      {children}
    </div>
  );
}
