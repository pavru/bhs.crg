import { useState, useRef, useLayoutEffect, useMemo } from 'react';
import { ChevronLeft, ChevronRight, ChevronDown } from 'lucide-react';
import type { DatePrecision } from '@/shared/api/types';
import { parseISO, toISO, daysInMonth, monthMatrix, dayInWeek, outOfRange, truncISO, MIN_YEAR, MAX_YEAR } from '@/shared/utils/date';
import { firstDayOfWeek, monthNames, spokenDate, weekdayNames } from '@/shared/format/dateMask';
import { companyToday } from '@/shared/format/companyDay';
import { resolveLocale, useLocale } from '@/shared/hooks/useLocale';
import { Button } from './Button';
import { Tooltip } from './Tooltip';

/**
 * MD3 docked-календарь (issue #338): чистая управляемая поверхность выбора даты. Три ТЕРМИНАЛЬНЫХ
 * вида по точности (#60): day → сетка месяца (Пн-Вс), month → сетка месяцев + год, year → сетка лет.
 * Вся APG-клавиатура (стрелки/PageUp-Down/Shift+Page/Home/End/Enter/Esc), тема на токенах. Названия
 * месяцев и дней недели и первый день недели — по языку пользователя (issue #1461), не таблицей.
 * Аддитивен к сегментному вводу — сюда приходит текущее value, отсюда уходит выбранный полный ISO.
 *
 * «Сегодня» — день компании, а не браузера (issue #1463): подсветка и месяц, с которого открывается
 * пустое поле, — по `companyToday`. Разметка — APG grid: строки `row`, клетки `gridcell` с
 * `aria-selected`, у дней — строка заголовков `columnheader`.
 *
 * Внизу — строка действий (issue #1463, макет «Очистить и Сегодня», вариант В): «Очистить» слева,
 * «Сегодня» справа. Выбор календарь не подтверждает — щелчок по дню сразу ставит дату, — поэтому
 * «Отмена/OK» из MD3 здесь нет, только эти два действия.
 *
 * Границы min/max (issue #1464): дни, месяцы и годы вне них неактивны, фокус по сетке и стрелки шапки
 * за них не заходят — у неактивной кнопки фокуса нет, и роллинг-фокус молча потерялся бы. Сравнение —
 * по точности (`outOfRange`). «Сегодня» вне границ скрыта, а не ставит другой день; пустое поле
 * открывается на ближайшей границе. `range` — лента периода у `DateRange`: от «с» до «по».
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
  /** Закрыть поповер (Esc / выбор в терминальном виде). */
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
  // Клавиатурный фокус (роллинг). d актуален в day-view; m — в day/month; y — везде.
  const [fy, setFy] = useState(clampYear(start.y));
  const [fm, setFm] = useState(start.m);
  const [fd, setFd] = useState(start.d);

  const gridRef = useRef<HTMLDivElement>(null);
  // После смены фокуса/вида — переводим DOM-фокус на активную ячейку (роллинг tabindex).
  useLayoutEffect(() => {
    gridRef.current?.querySelector<HTMLElement>('[data-focus="true"]')?.focus();
  }, [fy, fm, fd, view]);

  function selectDay(y: number, m: number, d: number) { onSelect(toISO(y, m, d)); onClose?.(); }
  function selectMonth(y: number, m: number) { onSelect(toISO(y, m, 1)); onClose?.(); }
  function selectYear(y: number) {
    if (precision === 'year') { onSelect(toISO(y, 1, 1)); onClose?.(); return; }
    // Год выбран → назад в терминальный вид; день и месяц фокуса — в границах нового года.
    focusOn(y, fm, Math.min(fd, daysInMonth(y, fm)), precision);
    setView(precision === 'month' ? 'months' : 'days');
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

  function onMonthsKey(e: React.KeyboardEvent) {
    let m = fm, y = fy;
    switch (e.key) {
      case 'ArrowLeft': if (m > 1) m--; else { m = 12; y = clampYear(y - 1); } break;
      case 'ArrowRight': if (m < 12) m++; else { m = 1; y = clampYear(y + 1); } break;
      case 'ArrowUp': if (m > 4) m -= 4; break;
      case 'ArrowDown': if (m <= 8) m += 4; break;
      case 'PageUp': y = clampYear(y - 1); break;
      case 'PageDown': y = clampYear(y + 1); break;
      case 'Enter': case ' ': selectMonth(fy, fm); e.preventDefault(); return;
      default: return;
    }
    focusOn(y, m, 1, 'month'); e.preventDefault();
  }

  const YEARS_PER_PAGE = 16;
  const pageStart = Math.floor((fy - MIN_YEAR) / YEARS_PER_PAGE) * YEARS_PER_PAGE + MIN_YEAR;
  function onYearsKey(e: React.KeyboardEvent) {
    switch (e.key) {
      case 'ArrowLeft': setFy(y => clampYear(y - 1)); break;
      case 'ArrowRight': setFy(y => clampYear(y + 1)); break;
      case 'ArrowUp': setFy(y => clampYear(y - 4)); break;
      case 'ArrowDown': setFy(y => clampYear(y + 4)); break;
      case 'PageUp': setFy(y => clampYear(y - YEARS_PER_PAGE)); break;
      case 'PageDown': setFy(y => clampYear(y + YEARS_PER_PAGE)); break;
      case 'Enter': case ' ': selectYear(fy); break;
      default: return;
    }
    e.preventDefault();
  }

  const cellBase = 'flex items-center justify-center rounded-full text-sm transition-colors focus:outline-none disabled:opacity-38 disabled:pointer-events-none';
  // Индикатор роллинг-фокуса (issue #338): виден ВСЕГДА (не только при :focus-visible), т.к. при
  // открытии мышью фокус на активной ячейке есть, но :focus-visible не срабатывает → «нет курсора».
  // На выбранной ячейке (bg-primary) — инсет-кольцо цветом текста на заливке, иначе — brand. Не белое:
  // в тёмной теме заливка светлая, и белое кольцо на ней не видно (issue #1462).
  const focusRing = (isFocus: boolean, isSel: boolean) =>
    !isFocus ? '' : isSel ? 'ring-2 ring-on-primary/70 ring-inset' : 'ring-2 ring-primary ring-inset';
  const HEADER_BTN = 'flex items-center justify-center h-8 w-8 rounded-full text-fg3 hover:text-on-surface hover:bg-black/5 dark:hover:bg-white/10 transition-colors disabled:opacity-38 disabled:pointer-events-none';
  // Стрелка шапки неактивна, если соседняя страница целиком за границей.
  const lastShown = view === 'years' ? pageStart + YEARS_PER_PAGE - 1 : fy;
  const prevOff = view === 'days' ? !!min && toISO(fy, fm, 1) <= min : (view === 'years' ? pageStart : fy) <= minY;
  const nextOff = view === 'days' ? !!max && toISO(fy, fm, daysInMonth(fy, fm)) >= max : lastShown >= maxY;
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

  // ── Шапка (навигация уровнем + переключение вида) ──
  const header = (
    <div className="flex items-center justify-between mb-2 px-1">
      <button type="button" aria-label="Назад" className={HEADER_BTN} disabled={prevOff}
        onClick={() => view === 'years' ? setFy(y => clampYear(y - YEARS_PER_PAGE)) : view === 'months' ? focusOn(fy - 1, fm, 1, 'month') : shiftMonth(-1)}>
        <ChevronLeft size={18} />
      </button>
      {view === 'years' ? (
        <span className="text-sm font-medium text-on-surface tabular-nums">
          {clampYear(pageStart)}–{clampYear(pageStart + YEARS_PER_PAGE - 1)}
        </span>
      ) : (
        <Tooltip content="Выбрать год">
          <button type="button" onClick={() => setView('years')}
            className="flex items-center gap-1 px-2 h-8 rounded-full text-sm font-medium text-on-surface hover:bg-black/5 dark:hover:bg-white/10 transition-colors">
            {view === 'days' ? `${months[fm - 1]} ${fy}` : fy}
            <ChevronDown size={14} className="text-fg4" />
          </button>
        </Tooltip>
      )}
      <button type="button" aria-label="Вперёд" className={HEADER_BTN} disabled={nextOff}
        onClick={() => view === 'years' ? setFy(y => clampYear(y + YEARS_PER_PAGE)) : view === 'months' ? focusOn(fy + 1, fm, 1, 'month') : shiftMonth(1)}>
        <ChevronRight size={18} />
      </button>
    </div>
  );

  // ── Тело по виду ──
  let body: React.ReactNode;
  if (view === 'days') {
    const weeks = monthMatrix(fy, fm, firstDay);
    body = (
      <div ref={gridRef} role="grid" aria-label="Календарь" onKeyDown={onDaysKey}>
        <div role="row" className="grid grid-cols-7 mb-1">
          {weekdays.map(w => <div key={w} role="columnheader" className="h-7 flex items-center justify-center text-xs text-fg4">{w}</div>)}
        </div>
        {weeks.map((week, wi) => (
          <div key={wi} role="row" className="grid grid-cols-7">
            {week.map((d, di) => {
              if (d == null) return <div key={di} role="gridcell" className="h-9" />;
              const iso = toISO(fy, fm, d);
              const isSel = !!sel && sel.y === fy && sel.m === fm && sel.d === d;
              const isToday = iso === todayISO;
              const isFocus = d === fd;
              const place = band(iso, 'day');
              const filled = isSel || place === 'start' || place === 'end';
              return (
                <div key={di} role="gridcell" aria-selected={isSel} className="h-9 flex items-center justify-center relative">
                  {place && <span aria-hidden className={`absolute inset-y-0.5 bg-primary-container ${BAND[place]}`} />}
                  <button type="button" data-focus={isFocus} tabIndex={isFocus ? 0 : -1}
                    aria-current={isToday ? 'date' : undefined} disabled={outOfRange(iso, min, max)}
                    onClick={() => selectDay(fy, fm, d)}
                    className={`${cellBase} relative h-9 w-9 ${focusRing(isFocus, filled)} ${
                      filled ? 'bg-primary text-on-primary font-medium'
                      : isToday ? `text-primary hover:bg-primary-container ${isFocus ? '' : 'ring-1 ring-primary'}`
                      : 'text-on-surface hover:bg-black/5 dark:hover:bg-white/10'}`}>
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
      <div ref={gridRef} role="grid" aria-label="Выбор месяца" onKeyDown={onMonthsKey} className={ROWS}>
        {gridRows(monthsShort.map((mn, i) => {
          const m = i + 1;
          const isSel = !!sel && sel.y === fy && sel.m === m && precision === 'month';
          const isCur = today.y === fy && today.m === m;
          const isFocus = m === fm;
          const place = precision === 'month' ? band(toISO(fy, m, 1), 'month') : null;
          const filled = isSel || place === 'start' || place === 'end';
          return (
            <div key={mn} role="gridcell" aria-selected={isSel} className="flex">
            <button type="button" data-focus={isFocus} tabIndex={isFocus ? 0 : -1}
              aria-current={isCur ? 'date' : undefined} onClick={() => selectMonth(fy, m)}
              disabled={outOfRange(toISO(fy, m, 1), min, max, 'month')}
              className={`${cellBase} flex-1 h-11 ${focusRing(isFocus, filled)} ${
                filled ? 'bg-primary text-on-primary font-medium' : place === 'mid' ? 'bg-primary-container text-on-surface'
                : isCur ? `text-primary hover:bg-primary-container ${isFocus ? '' : 'ring-1 ring-primary'}`
                : 'text-on-surface hover:bg-black/5 dark:hover:bg-white/10'}`}>
              {mn}
            </button>
            </div>
          );
        }))}
      </div>
    );
  } else {
    body = (
      <div ref={gridRef} role="grid" aria-label="Выбор года" onKeyDown={onYearsKey} className={ROWS}>
        {gridRows(Array.from({ length: YEARS_PER_PAGE }, (_, i) => pageStart + i).filter(y => y <= MAX_YEAR).map(y => {
          const isSel = !!sel && sel.y === y && precision === 'year';
          const isCur = y === today.y;
          const isFocus = y === fy;
          const place = precision === 'year' ? band(toISO(y, 1, 1), 'year') : null;
          const filled = isSel || place === 'start' || place === 'end';
          return (
            <div key={y} role="gridcell" aria-selected={isSel} className="flex">
            <button type="button" data-focus={isFocus} tabIndex={isFocus ? 0 : -1}
              aria-current={isCur ? 'date' : undefined} onClick={() => selectYear(y)}
              disabled={outOfRange(toISO(y, 1, 1), min, max, 'year')}
              className={`${cellBase} flex-1 h-11 tabular-nums ${focusRing(isFocus, filled)} ${
                filled ? 'bg-primary text-on-primary font-medium' : place === 'mid' ? 'bg-primary-container text-on-surface'
                : isCur ? `text-primary hover:bg-primary-container ${isFocus ? '' : 'ring-1 ring-primary'}`
                : 'text-on-surface hover:bg-black/5 dark:hover:bg-white/10'}`}>
              {y}
            </button>
            </div>
          );
        }))}
      </div>
    );
  }

  // «Сегодня» — день компании, скрытые точностью части — 01, как у выбора. Имя для чтения с экрана
  // называет дату: около полуночи день компании расходится с часами компьютера, и голое «Сегодня» не
  // говорит, какой день встанет. Видимой подписи хватает слова — день показывает кольцо в сетке.
  const todayLabel = TODAY_LABEL[precision];
  const todayPick = precision === 'year' ? toISO(today.y, 1, 1) : precision === 'month' ? toISO(today.y, today.m, 1) : todayISO;
  // Сегодня за границей — кнопки нет: «Сегодня», ставящая другой день, обещала бы не то (#1464).
  const showToday = !outOfRange(todayPick, min, max, precision);

  return (
    <div className="w-72 p-2 select-none">
      {header}
      {body}
      {(onClear || showToday) && (
        <div className="flex items-center gap-2 mt-2 pt-2 border-t border-outline-variant">
          {onClear && <Button variant="text" size="sm" onClick={onClear}>Очистить</Button>}
          {showToday && (
            <Button variant="text" size="sm" className="ml-auto" aria-label={`${todayLabel}, ${spokenDate(todayISO, locale, precision)}`}
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

/** Сетка месяцев и лет — строками по четыре: у `grid` клетки лежат в `row`, а не прямо в нём (APG). */
const ROWS = 'flex flex-col gap-1';
function gridRows(cells: React.ReactNode[]): React.ReactNode {
  const rows: React.ReactNode[] = [];
  for (let i = 0; i < cells.length; i += 4)
    rows.push(<div key={i} role="row" className="grid grid-cols-4 gap-1">{cells.slice(i, i + 4)}</div>);
  return rows;
}
