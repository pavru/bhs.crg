import { useState, useRef, useLayoutEffect } from 'react';
import { ChevronLeft, ChevronRight, ChevronDown } from 'lucide-react';
import type { DatePrecision } from '@/shared/api/types';
import {
  MONTHS_RU, MONTHS_RU_SHORT, WEEKDAYS_RU, parseISO, toISO, daysInMonth, monthMatrix,
} from '@/shared/utils/date';

/**
 * MD3 docked-календарь (issue #338): чистая управляемая поверхность выбора даты. Три ТЕРМИНАЛЬНЫХ
 * вида по точности (#60): day → сетка месяца (Пн-Вс), month → сетка месяцев + год, year → сетка лет.
 * Вся APG-клавиатура (стрелки/PageUp-Down/Shift+Page/Home/End/Enter/Esc), RU-локаль, тема на токенах.
 * Аддитивен к сегментному вводу — сюда приходит текущее value, отсюда уходит выбранный полный ISO.
 */
const MIN_YEAR = 1900;
const MAX_YEAR = 2100;
const clampYear = (y: number) => Math.min(MAX_YEAR, Math.max(MIN_YEAR, y));

type View = 'days' | 'months' | 'years';

export function Calendar({ value, onSelect, precision = 'day', onClose }: {
  /** Текущее значение — полный ISO YYYY-MM-DD или ''. */
  value: string;
  /** Выбор завершён → полный ISO (скрытые точностью части = 01, как в сегментном вводе). */
  onSelect: (iso: string) => void;
  precision?: DatePrecision;
  /** Закрыть поповер (Esc / выбор в терминальном виде). */
  onClose?: () => void;
}) {
  const now = new Date();
  const sel = parseISO(value);
  const start = sel ?? { y: now.getFullYear(), m: now.getMonth() + 1, d: now.getDate() };

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

  const todayISO = toISO(now.getFullYear(), now.getMonth() + 1, now.getDate());

  function selectDay(y: number, m: number, d: number) { onSelect(toISO(y, m, d)); onClose?.(); }
  function selectMonth(y: number, m: number) { onSelect(toISO(y, m, 1)); onClose?.(); }
  function selectYear(y: number) {
    if (precision === 'year') { onSelect(toISO(y, 1, 1)); onClose?.(); return; }
    setFy(y); setView(precision === 'month' ? 'months' : 'days'); // год выбран → назад в терминальный вид
  }

  // ── day-view: перемещение фокуса по дням (через нативный Date — корректный роллинг) ──
  function shiftDay(delta: number) {
    const dt = new Date(fy, fm - 1, fd); dt.setDate(dt.getDate() + delta);
    const y = clampYear(dt.getFullYear());
    setFy(y); setFm(dt.getMonth() + 1); setFd(dt.getDate());
  }
  function shiftMonth(delta: number) {
    const dt = new Date(fy, fm - 1 + delta, 1);
    const y = clampYear(dt.getFullYear()), m = dt.getMonth() + 1;
    setFy(y); setFm(m); setFd(Math.min(fd, daysInMonth(y, m)));
  }

  function onDaysKey(e: React.KeyboardEvent) {
    switch (e.key) {
      case 'ArrowLeft': shiftDay(-1); break;
      case 'ArrowRight': shiftDay(1); break;
      case 'ArrowUp': shiftDay(-7); break;
      case 'ArrowDown': shiftDay(7); break;
      case 'Home': shiftDay(-(((new Date(fy, fm - 1, fd).getDay() + 6) % 7))); break; // к понедельнику недели
      case 'End': shiftDay(6 - ((new Date(fy, fm - 1, fd).getDay() + 6) % 7)); break;  // к воскресенью
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
    setFm(m); setFy(y); e.preventDefault();
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

  const cellBase = 'flex items-center justify-center rounded-full text-sm transition-colors focus:outline-none focus-visible:ring-2 focus-visible:ring-brand disabled:opacity-30 disabled:pointer-events-none';
  const HEADER_BTN = 'flex items-center justify-center h-8 w-8 rounded-full text-fg3 hover:text-fg1 hover:bg-black/5 dark:hover:bg-white/10 transition-colors';

  // ── Шапка (навигация уровнем + переключение вида) ──
  const header = (
    <div className="flex items-center justify-between mb-2 px-1">
      <button type="button" aria-label="Назад" className={HEADER_BTN}
        onClick={() => view === 'years' ? setFy(y => clampYear(y - YEARS_PER_PAGE)) : view === 'months' ? setFy(y => clampYear(y - 1)) : shiftMonth(-1)}>
        <ChevronLeft size={18} />
      </button>
      {view === 'years' ? (
        <span className="text-sm font-medium text-fg1 tabular-nums">
          {clampYear(pageStart)}–{clampYear(pageStart + YEARS_PER_PAGE - 1)}
        </span>
      ) : (
        <button type="button" onClick={() => setView('years')}
          className="flex items-center gap-1 px-2 h-8 rounded-full text-sm font-medium text-fg1 hover:bg-black/5 dark:hover:bg-white/10 transition-colors"
          title="Выбрать год">
          {view === 'days' ? `${MONTHS_RU[fm - 1]} ${fy}` : fy}
          <ChevronDown size={14} className="text-fg4" />
        </button>
      )}
      <button type="button" aria-label="Вперёд" className={HEADER_BTN}
        onClick={() => view === 'years' ? setFy(y => clampYear(y + YEARS_PER_PAGE)) : view === 'months' ? setFy(y => clampYear(y + 1)) : shiftMonth(1)}>
        <ChevronRight size={18} />
      </button>
    </div>
  );

  // ── Тело по виду ──
  let body: React.ReactNode;
  if (view === 'days') {
    const weeks = monthMatrix(fy, fm);
    body = (
      <div ref={gridRef} role="grid" aria-label="Календарь" onKeyDown={onDaysKey}>
        <div className="grid grid-cols-7 mb-1">
          {WEEKDAYS_RU.map(w => <div key={w} className="h-7 flex items-center justify-center text-xs text-fg4">{w}</div>)}
        </div>
        {weeks.map((week, wi) => (
          <div key={wi} role="row" className="grid grid-cols-7">
            {week.map((d, di) => {
              if (d == null) return <div key={di} className="h-9" />;
              const iso = toISO(fy, fm, d);
              const isSel = !!sel && sel.y === fy && sel.m === fm && sel.d === d;
              const isToday = iso === todayISO;
              const isFocus = d === fd;
              return (
                <div key={di} role="gridcell" className="h-9 flex items-center justify-center">
                  <button type="button" data-focus={isFocus} tabIndex={isFocus ? 0 : -1}
                    aria-selected={isSel} aria-current={isToday ? 'date' : undefined}
                    onClick={() => selectDay(fy, fm, d)}
                    className={`${cellBase} h-9 w-9 ${
                      isSel ? 'bg-brand text-white font-medium'
                      : isToday ? 'ring-1 ring-brand text-brand hover:bg-brand-subtle'
                      : 'text-fg1 hover:bg-black/5 dark:hover:bg-white/10'}`}>
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
      <div ref={gridRef} role="grid" aria-label="Выбор месяца" onKeyDown={onMonthsKey} className="grid grid-cols-4 gap-1">
        {MONTHS_RU_SHORT.map((mn, i) => {
          const m = i + 1;
          const isSel = !!sel && sel.y === fy && sel.m === m && precision === 'month';
          const isCur = now.getFullYear() === fy && now.getMonth() + 1 === m;
          const isFocus = m === fm;
          return (
            <button key={mn} type="button" data-focus={isFocus} tabIndex={isFocus ? 0 : -1}
              aria-selected={isSel} onClick={() => selectMonth(fy, m)}
              className={`${cellBase} h-11 ${
                isSel ? 'bg-brand text-white font-medium'
                : isCur ? 'ring-1 ring-brand text-brand hover:bg-brand-subtle'
                : 'text-fg1 hover:bg-black/5 dark:hover:bg-white/10'}`}>
              {mn}
            </button>
          );
        })}
      </div>
    );
  } else {
    body = (
      <div ref={gridRef} role="grid" aria-label="Выбор года" onKeyDown={onYearsKey} className="grid grid-cols-4 gap-1">
        {Array.from({ length: YEARS_PER_PAGE }, (_, i) => pageStart + i).filter(y => y <= MAX_YEAR).map(y => {
          const isSel = !!sel && sel.y === y && precision === 'year';
          const isCur = y === now.getFullYear();
          const isFocus = y === fy;
          return (
            <button key={y} type="button" data-focus={isFocus} tabIndex={isFocus ? 0 : -1}
              aria-selected={isSel} onClick={() => selectYear(y)}
              className={`${cellBase} h-11 tabular-nums ${
                isSel ? 'bg-brand text-white font-medium'
                : isCur ? 'ring-1 ring-brand text-brand hover:bg-brand-subtle'
                : 'text-fg1 hover:bg-black/5 dark:hover:bg-white/10'}`}>
              {y}
            </button>
          );
        })}
      </div>
    );
  }

  return (
    <div className="w-72 p-2 select-none">
      {header}
      {body}
    </div>
  );
}
