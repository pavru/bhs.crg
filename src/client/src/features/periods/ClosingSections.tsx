import { ArrowUpRight, TriangleAlert } from 'lucide-react';
import type { ClosingLine, ClosingSection } from '@/shared/api/periods';
import { formatMoney } from '@/shared/format/format';
import { figure, goneLines, goneSections, localLink, unfinishedSummary, wasText, type ClosingGroup } from './closing';

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
 * Ссылку строке даёт модуль — и только там, где под ней те же число и сумма. Открывается она в новой
 * вкладке: диалог с датами остаётся на месте.
 *
 * Тот же перечень показывает «История» — из записи о закрытии.
 *
 * @param before Перечень, который человек видел до отказа «данные изменились»: изменившиеся строки
 * говорят, какими были.
 */
export function ClosingSections({ sections, stale, before = null }: {
  sections: ClosingSection[]; stale?: boolean; before?: ClosingSection[] | null;
}) {
  const gone = goneSections(before, sections);
  if (sections.length === 0 && gone.length === 0)
    return <p className="text-[13px] text-fg3">Включённых модулей учёта нет. Закрытие запишет только границу периода.</p>;

  return (
    // Пока идёт пересчёт, прежние числа приглушены, а не убраны: «ещё не знаем» — не «пусто».
    <div className={`space-y-4 ${stale ? 'opacity-50' : ''}`} aria-busy={stale}>
      {sections.map(section => (
        <section key={section.module} aria-label={section.title}>
          <h3 className="text-sm font-semibold text-fg1">{section.title}</h3>
          <p className="text-[12px] text-fg3">{section.dateRule}</p>

          <Group title="Не завершено — закрытию не мешает" group="unfinished" section={section} before={before} warn
            empty="Незавершённого нет." />
          <Group title="Войдёт в закрытый период" group="frozen" section={section} before={before}
            empty="В эти дни ничего не попадает — замораживать нечего." />

          {section.amountsHidden && (
            <p className="mt-1.5 text-[12px] text-fg3">Суммы не показаны: у вас нет права их видеть.</p>
          )}
        </section>
      ))}
      {/* Раздел, исчезнувший целиком, — тоже изменение: без него его «не завершено» пропало бы молча. */}
      {gone.map(section => (
        <section key={`gone:${section.module}`} aria-label={section.title} className="text-[13px] text-fg3">
          <h3 className="text-sm font-semibold text-fg2">{section.title}</h3>
          <p>
            Раздела больше нет: модуль о закрытии уже ничего не сообщает.
            {unfinishedSummary([section]) && <Was text={`было не завершено: ${unfinishedSummary([section])}`} />}
          </p>
        </section>
      ))}
    </div>
  );
}

function Group({ title, group, section, before, empty, warn }: {
  title: string; group: ClosingGroup; section: ClosingSection; before: ClosingSection[] | null;
  empty: string; warn?: boolean;
}) {
  const lines = section[group];
  const gone = goneLines(before, section, group);

  return (
    <div className="mt-2">
      <h4 className="text-[12px] font-medium uppercase tracking-wide text-fg3">{title}</h4>
      {lines.length === 0 && gone.length === 0 ? (
        <p className="text-[13px] text-fg3">{empty}</p>
      ) : (
        <ul className="mt-0.5 space-y-1">
          {lines.map(line => (
            <li key={line.key} className="text-[13px]">
              <div className={`flex items-baseline gap-1.5 ${warn ? 'text-fg1' : 'text-fg2'}`}>
                {warn && <TriangleAlert size={13} className="shrink-0 self-center text-warning" aria-hidden />}
                <span className="min-w-0 break-words">
                  {line.text}: <Figure line={line} />
                  <Was text={wasText(before, section.module, group, line)} />
                </span>
              </div>
              {line.note && <p className={`text-[12px] text-fg3 ${warn ? 'pl-5' : ''}`}>{line.note}</p>}
            </li>
          ))}
          {/* Исчезнувшая строка — тоже изменение: без неё «было 2 счёта» пропало бы молча. */}
          {gone.map(line => (
            <li key={`gone:${line.key}`} className="text-[13px] text-fg3">
              <span className={warn ? 'pl-5' : ''}>
                {line.text}: теперь нет <Was text={`было: ${figure(line)}`} />
              </span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

/** Число и сумма строки; со ссылкой модуля — ссылкой на экран, где эти документы перечислены. */
function Figure({ line }: { line: ClosingLine }) {
  const text = (
    <>
      <span className="tabular-nums font-medium">{line.counted}</span>
      {line.amount !== null && <span className="tabular-nums"> на {formatMoney(line.amount)}</span>}
    </>
  );
  const link = localLink(line.link);
  if (!link) return text;

  return (
    <a href={link} target="_blank" rel="noreferrer" title="Открыть список — в новой вкладке"
      className="text-brand hover:underline">
      {text}
      <ArrowUpRight size={12} className="inline-block align-baseline ml-0.5" aria-hidden />
    </a>
  );
}

function Was({ text }: { text: string | null }) {
  if (!text) return null;
  return <span className="ml-1.5 rounded bg-warning/15 px-1 text-[12px] text-fg2">{text}</span>;
}
