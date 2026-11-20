import { useState, type ReactNode } from 'react';
import { firstDayOfWeek } from '@/shared/format/dateMask';
import { formatDate, formatDayMonth, formatLocale } from '@/shared/format/format';
import { Button, IconButton } from './Button';
import { Icon } from './Icon';
import { ChevronLeft, ChevronRight } from './icons';
import { Tooltip } from './Tooltip';
import { barPlace, isoOf, timelineWindow } from './timelineWindow';

/**
 * Лента периодов (change `work-12-assignments-ui`, макет Р2, вариант А+): строка — подпись, полоса — период по
 * неделям, черта — «сегодня». Общий компонент: им же рисуется график этапа 3 (`plan-20`).
 *
 * Окно — `weeks` недель от недели за две до «сегодня»; «Раньше», «Позже» листают по неделе, «Сегодня» —
 * возвращает. Положение полосы — только даты строки: лента ничего не сравнивает и пересечений не ищет — их
 * называет тот, кто даёт строки (у назначений — сервер при записи). Даты — ISO без времени, «сегодня» — тоже
 * датой: чьё это сегодня (пояс стройки), решает потребитель.
 *
 * <p>Доступность. Полоса — кнопка с именем `aria` строки (подпись короткой полосы в неё не влезает, имя — всегда
 * полное); строка без `onOpen` рисует полосу без кнопки. Период целиком вне окна полосы не рисует, но строка
 * остаётся и говорит «вне окна» с периодом — строка не пропадает молча. Ряд действий строки (`actions`) — свой
 * столбец справа, как у сетки.</p>
 */

/** Тон полосы — ролью MD3, у каждого своя пометка в легенде потребителя. */
export type TimelineTone = 'primary' | 'secondary' | 'outline' | 'muted';

export interface TimelineRow {
  key: string;
  label: ReactNode;
  sublabel?: ReactNode;
  /** ISO-дата начала. */
  from: string;
  /** ISO-дата конца; `null` — без срока (полоса до края со стрелкой). */
  to: string | null;
  tone: TimelineTone;
  /** Подпись на полосе — короткая: у узкой полосы она обрезается. */
  barLabel: string;
  /** Имя полосы для читалки и подсказка — полное. */
  aria: string;
  onOpen?: () => void;
  actions?: ReactNode;
}

const TONE: Record<TimelineTone, string> = {
  primary: 'bg-primary-container text-on-primary-container',
  secondary: 'bg-secondary-container text-on-secondary-container',
  outline: 'border border-outline text-on-surface-variant bg-surface',
  muted: 'bg-surface-container-highest text-on-surface-variant',
};

export function Timeline({ rows, today, weeks = 8, nameLabel, 'aria-label': ariaLabel }: {
  rows: TimelineRow[];
  /** «Сегодня» — ISO-дата. */
  today: string;
  weeks?: number;
  /** Подпись столбца строк — «Сотрудник». */
  nameLabel: string;
  'aria-label': string;
}) {
  const [offset, setOffset] = useState(0);
  const firstDay = firstDayOfWeek(formatLocale());
  const { start, days, end, todayAt } = timelineWindow(today, offset, weeks, firstDay);
  const todayShown = todayAt >= 0 && todayAt < days;

  return (
    <section aria-label={ariaLabel} className="space-y-2">
      <div className="flex flex-wrap items-center gap-1">
        <IconButton icon={ChevronLeft} label="Раньше на неделю" onClick={() => setOffset(o => o - 1)} />
        <span className="text-title-small text-on-surface tabular-nums" aria-live="polite">
          {formatDate(isoOf(start))} — {formatDate(isoOf(end))}
        </span>
        <IconButton icon={ChevronRight} label="Позже на неделю" onClick={() => setOffset(o => o + 1)} />
        <Button variant="text" disabled={offset === 0} onClick={() => setOffset(0)}>Сегодня</Button>
      </div>
      <div className="overflow-hidden rounded-md border border-outline-variant bg-surface">
        <div className="flex h-10 items-center border-b border-outline-variant text-label-medium text-on-surface-variant" aria-hidden>
          <span className="w-60 shrink-0 px-4">{nameLabel}</span>
          <span className="flex min-w-0 flex-1">
            {Array.from({ length: weeks }, (_, i) => (
              <span key={i} className="flex-1 border-l border-outline-variant px-2 tabular-nums">
                {formatDayMonth(isoOf(start + i * 7))}
              </span>
            ))}
          </span>
          <span className="w-12 shrink-0" />
        </div>
        <ul>
          {rows.map(r => {
            const place = barPlace(r.from, r.to, start, days);
            return (
              <li key={r.key} className="flex min-h-13 items-center border-t border-outline-variant first:border-t-0">
                <span className="flex w-60 shrink-0 flex-col px-4 py-1">
                  <span className="text-body-medium text-on-surface">{r.label}</span>
                  {r.sublabel && <span className="text-body-small text-on-surface-variant">{r.sublabel}</span>}
                </span>
                <span className="relative h-13 min-w-0 flex-1">
                  {Array.from({ length: weeks - 1 }, (_, i) => (
                    <span key={i} aria-hidden className="absolute inset-y-0 w-px bg-outline-variant/60"
                      style={{ left: `${((i + 1) * 100) / weeks}%` }} />
                  ))}
                  {todayShown && (
                    <span aria-hidden data-today className="absolute inset-y-0 w-0.5 bg-primary"
                      style={{ left: `${((todayAt + 0.5) * 100) / days}%` }} />
                  )}
                  {place ? (
                    <Bar row={r} place={place} />
                  ) : (
                    <span className="absolute inset-0 flex items-center px-3 text-body-small text-on-surface-variant">
                      вне окна · {r.barLabel}
                    </span>
                  )}
                </span>
                <span className="flex w-12 shrink-0 justify-center">{r.actions}</span>
              </li>
            );
          })}
        </ul>
      </div>
    </section>
  );
}

function Bar({ row, place }: { row: TimelineRow; place: NonNullable<ReturnType<typeof barPlace>> }) {
  const shape = `${place.cutStart ? 'rounded-l-none' : 'rounded-l-sm'} ${place.cutEnd ? 'rounded-r-none' : 'rounded-r-sm'}`;
  const box = `absolute top-2.5 flex h-8 items-center gap-1 overflow-hidden px-2 text-label-medium whitespace-nowrap ${TONE[row.tone]} ${shape}`;
  const style = { left: `calc(${place.left * 100}% + 2px)`, width: `calc(${place.width * 100}% - 4px)` };
  const content = (
    <>
      <span className="min-w-0 truncate">{row.barLabel}</span>
      {place.open && <Icon icon={ChevronRight} size={18} aria-hidden />}
    </>
  );
  return row.onOpen ? (
    <Tooltip content={row.aria}>
      <button type="button" className={`${box} state-layer focus-ring`} style={style} aria-label={row.aria} onClick={row.onOpen}>
        {content}
      </button>
    </Tooltip>
  ) : (
    <Tooltip content={row.aria}>
      <span className={box} style={style} role="img" aria-label={row.aria}>{content}</span>
    </Tooltip>
  );
}
