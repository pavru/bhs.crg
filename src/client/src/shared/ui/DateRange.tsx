import { useEffect, useRef, useState } from 'react';
import type { DatePrecision } from '@/shared/api/types';
import { truncISO } from '@/shared/utils/date';
import { DateInput } from './DateInput';

/**
 * Период из двух дат (issue #1464; макет «Обвязка, границы, диапазон» — вариант А, лента из Б, выбор
 * владельца 21.10.2026): два плотных поля 40 ([UI-69]) через «—», подпись группы — над парой. Узкий контейнер
 * (телефон) ставит поля столбиком — по своей ширине, а не по экрану: пара живёт и в узкой колонке.
 *
 * <p>«по» не раньше «с». Побеждает граница, которую правили: поздняя «с» тянет за собой «по», ранняя
 * «по» — «с». Поправка — при фиксации (уход из поля, выбор в календаре), а не на каждой отданной дате:
 * иначе ↓ по дню утянула бы другую границу, и ↑ её уже не вернула бы. Стёртая граница другую не
 * трогает. В календаре «по» дни раньше «с» неактивны; календарь «с» свободен — иначе сдвинуть период
 * вперёд можно было бы, только начав с «по». Оба календаря рисуют ленту периода.</p>
 */
export function DateRange({
  label, labelHidden = false, from, to, onChange, precision = 'day', min, max, disabled, readOnly, required, ordered = false, className = '',
}: {
  /** Подпись группы — legend над парой; полям она же с «с» / «по» для чтения с экрана. */
  label: string;
  /**
   * Подпись — только для чтения с экрана: пару уже называет строка вокруг (оператор «между» в отборе,
   * issue #1465). Видимая legend над парой сдвинула бы её с ряда колонки и оператора.
   */
  labelHidden?: boolean;
  /** Границы — полный ISO или ''. */
  from: string;
  to: string;
  onChange: (from: string, to: string) => void;
  precision?: DatePrecision;
  /** Внешние границы допустимых дат — у обоих полей. */
  min?: string;
  max?: string;
  disabled?: boolean;
  readOnly?: boolean;
  /** Пустой границы не бывает (период отчёта, issue #1466): ни крестика, ни «Очистить» в календаре. */
  required?: boolean;
  /**
   * Наружу — только упорядоченная пара (issue #1466): «по» раньше «с» держится здесь до фиксации, которая
   * её и поправит. Для приёмника, который пару «наоборот» отвергает, — адрес отчёта, который сервер
   * переспрашивает на каждой отданной паре. Без него пара уходит как набрана: форма отбора её хранит.
   */
  ordered?: boolean;
  className?: string;
}) {
  // Поправка приходит из таймера ухода — границы читаются на сейчас, а не из замыкания того рендера.
  // Только после правки: поле не фиксирует нетронутое значение, и сохранённая пара «наоборот»
  // при проходе Tab'ом остаётся как есть.
  const later = (a: string, b: string) => truncISO(a, precision) > truncISO(b, precision);
  // Удержанная пара «наоборот» (`ordered`) — только при тех границах, при которых набрана: пришли другие
  // («назад», ссылка) — показываются они.
  const [held, setHeld] = useState<{ base: string; from: string; to: string } | null>(null);
  const base = `${from}|${to}`;
  const pair = ordered && held?.base === base ? held : { from, to };
  const cur = useRef(pair);
  useEffect(() => { cur.current = pair; });
  const give = (f: string, t: string) => {
    if (ordered && f && t && later(f, t)) { setHeld({ base, from: f, to: t }); return; }
    setHeld(null);
    onChange(f, t);
  };
  const commitFrom = (v: string) => { const t = cur.current.to; if (v && t && later(v, t)) give(v, v); };
  const commitTo = (v: string) => { const f = cur.current.from; if (v && f && later(f, v)) give(v, v); };

  const range: [string, string] | undefined = pair.from && pair.to ? [pair.from, pair.to] : undefined;
  const toMin = pair.from && (!min || pair.from > min) ? pair.from : min;
  const common = { precision, density: 'dense', max, disabled, readOnly, required, range, className: 'w-full' } as const;
  return (
    <fieldset className={`min-w-0 ${className}`}>
      <legend className={labelHidden ? 'sr-only' : 'mb-1 text-body-medium font-medium text-on-surface'}>{label}</legend>
      <div className="@container">
        <div className="grid gap-1.5 items-center @[24rem]:grid-cols-[minmax(0,1fr)_auto_minmax(0,1fr)]">
          <DateInput label={`${label}, с`} value={pair.from} onChange={v => give(v, pair.to)} onCommit={commitFrom}
            {...common} min={min} />
          <span aria-hidden className="hidden @[24rem]:block text-on-surface-variant">—</span>
          <DateInput label={`${label}, по`} value={pair.to} onChange={v => give(pair.from, v)} onCommit={commitTo}
            {...common} min={toMin} />
        </div>
      </div>
    </fieldset>
  );
}
