import { TriangleAlert } from 'lucide-react';
import type { ClosingLine, ClosingSection } from '@/shared/api/periods';
import { formatMoney } from '@/shared/utils/money';

/**
 * Перечень диалога закрытия периода: по разделу на модуль — что не завершено и что войдёт в закрытый
 * период (ТЗ CORE-35; задача E1b, issue #1099).
 *
 * «Не завершено» стоит первым и с предупреждающим значком: закрытию оно не мешает, но после закрытия
 * останется как есть. «Войдёт в период» — справка. Правило даты — серой подписью раздела: по нему
 * человек понимает, почему счёт, выставленный в сентябре, попал в октябрь.
 *
 * Тексты строк — модуля, а не экрана: экран ядра не знает, что такое «счёт». Поэтому и о скрытых
 * суммах сказано без названия права: право называет модуль, и у другого модуля оно будет другим.
 *
 * Тот же перечень показывает «История» — из записи о закрытии.
 */
export function ClosingSections({ sections, stale }: { sections: ClosingSection[]; stale?: boolean }) {
  if (sections.length === 0)
    return <p className="text-[13px] text-fg3">Включённых модулей учёта нет. Закрытие запишет только границу периода.</p>;

  return (
    // Пока идёт пересчёт, прежние числа приглушены, а не убраны: «ещё не знаем» — не «пусто».
    <div className={`space-y-4 ${stale ? 'opacity-50' : ''}`} aria-busy={stale}>
      {sections.map(section => (
        <section key={section.module} aria-label={section.title}>
          <h3 className="text-sm font-semibold text-fg1">{section.title}</h3>
          <p className="text-[12px] text-fg3">{section.dateRule}</p>

          <Group title="Не завершено — закрытию не мешает" lines={section.unfinished} warn
            empty="Незавершённого нет." />
          <Group title="Войдёт в закрытый период" lines={section.frozen}
            empty="В эти дни ничего не попадает — замораживать нечего." />

          {section.amountsHidden && (
            <p className="mt-1.5 text-[12px] text-fg3">Суммы не показаны: у вас нет права их видеть.</p>
          )}
        </section>
      ))}
    </div>
  );
}

function Group({ title, lines, empty, warn }: { title: string; lines: ClosingLine[]; empty: string; warn?: boolean }) {
  return (
    <div className="mt-2">
      <h4 className="text-[12px] font-medium uppercase tracking-wide text-fg3">{title}</h4>
      {lines.length === 0 ? (
        <p className="text-[13px] text-fg3">{empty}</p>
      ) : (
        <ul className="mt-0.5 space-y-1">
          {lines.map(line => (
            <li key={line.key} className="text-[13px]">
              <div className={`flex items-baseline gap-1.5 ${warn ? 'text-fg1' : 'text-fg2'}`}>
                {warn && <TriangleAlert size={13} className="shrink-0 self-center text-warning" aria-hidden />}
                <span>
                  {line.text}: <span className="tabular-nums font-medium">{line.counted}</span>
                  {line.amount !== null && <span className="tabular-nums"> на {formatMoney(line.amount)}</span>}
                </span>
              </div>
              {line.note && <p className={`text-[12px] text-fg3 ${warn ? 'pl-5' : ''}`}>{line.note}</p>}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
