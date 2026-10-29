import { Tabs } from '@/shared/ui/Tabs';
import type { SiteCosts } from '@/shared/api/siteCosts';
import { formatMoney } from '@/shared/format/format';

/** Основа идентификаторов вкладок взгляда: панель затрат ссылается на неё же (`TabPanel`). */
export const COST_VIEWS_ID = 'site-costs-view';

type View = 'corrected' | 'closing';

/**
 * Полоса двух взглядов «Затрат по стройке» (E2, issue #1100, PR 6b-3; разбор Дизайнера § «Клиент
 * исправления счёта», п. 7).
 *
 * <p>Не в ряду отборов, а над таблицей затрат и только когда взгляды различаются: третий сегмент в ряду
 * отборов появлялся бы и исчезал незаметно. Совпали — ни полосы, ни переключателя: числа одни.</p>
 *
 * <p>Группа зовётся «Закрытые периоды», а не «взгляд»: «на момент закрытия» тогда не утверждает, что
 * выбранный месяц закрыт. Первый взгляд — «с исправлениями» (решение владельца 15.10.2026): это числа
 * реестра, со стрелками; умолчание «по закрытости месяца» спрятало бы исправление и в сентябре, и в
 * октябре. Итог другого взгляда стоит рядом — «закрытый месяц изменился» не бывает молчаливым.</p>
 */
export function CostViewsBand({ data, closing, onChange }: {
  data: SiteCosts;
  /**
   * Выбранный взгляд — из адреса, а не из ответа (ревью PR #1390): пока новый ответ в пути, на экране
   * прежние числа, и вкладка, выбранная по ним, не сдвинулась бы от щелчка до самого ответа.
   */
  closing: boolean;
  onChange: (view: View) => void;
}) {
  if (!data.differs) return null;
  const view: View = closing ? 'closing' : 'corrected';

  return (
    <section aria-label="Закрытые периоды" className="rounded-md border border-outline-variant bg-surface-container px-3 py-2 space-y-1.5 text-sm">
      <p className="text-on-surface-variant">
        В периоде есть исправления после закрытия: {data.differs === 'totals'
          ? 'итоги взглядов различаются.'
          : 'итоги равны, различаются разделы, поставщики или дни.'}
      </p>
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1.5">
        <span className="text-xs text-on-surface-variant">Закрытые периоды</span>
        <Tabs id={COST_VIEWS_ID} variant="segmented" aria-label="Закрытые периоды" value={view} onChange={onChange}
          items={[{ value: 'corrected', label: 'С исправлениями' }, { value: 'closing', label: 'На момент закрытия' }]} />
        {/* При равных итогах число соседа совпало бы с итогом на экране — сказано словами выше. */}
        {data.differs === 'totals' && data.other && (
          <span className="text-xs text-on-surface-variant">
            в другом взгляде итог {formatMoney(data.other.amount)}
          </span>
        )}
      </div>
      <p className="text-xs text-on-surface-variant">
        {view === 'closing'
          ? 'Закрытые месяцы — как при закрытии; исправления — корректировкой в месяце, когда сделаны. Стрелка в реестр — '
            + 'только у чисел, равных ему: реестр показывает счета как они есть сейчас.'
          : 'Исправления учтены в исправленных месяцах — как в реестре.'}
      </p>
    </section>
  );
}
