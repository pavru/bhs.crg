import { DatabaseZap, ScanText } from 'lucide-react';
import type { DataOrigin } from '@/shared/api/types';

/**
 * Значок «откуда это значение» у поля, заполняемого источником данных.
 *
 * Значок ОДИН и меняет форму, а не добавляется рядом: у поля и так конкурируют метки о поломках
 * (нерешённые ссылки, нарушенные ограничения, обязательность), и шестая отняла бы у них внимание.
 * Цветом различие тоже не передаётся — warning и danger в проекте означают «есть оговорка» и
 * «данные испорчены», а происхождение не про поломку: здесь ничего не сломано.
 *
 * Показывается только распознанное. `Parsed` и `System` оставляют прежний вид: у них нет действия
 * для читателя, а метка без действия превращается в фон.
 */
export function SourceOriginIcon({ origin, plural }: { origin?: DataOrigin; plural?: boolean }) {
  const recognized = origin === 'Recognized';
  const title = recognized
    ? plural
      ? 'Строки распознаны со скана — сверьте с оригиналом'
      : 'Значение распознано со скана — сверьте с оригиналом'
    : plural
      ? 'Значения подставляются из источника данных'
      : 'Значение подставляется из источника данных';

  return (
    <span title={title}>
      {recognized
        ? <ScanText size={12} className="text-brand" aria-hidden />
        : <DatabaseZap size={12} className="text-brand" aria-hidden />}
      {/* Подсказка на span доступна только мышью — тем, кто ведёт форму с клавиатуры, признак
          обязан достаться текстом (проект keyboard-first, issue #107). */}
      <span className="sr-only">{title}</span>
    </span>
  );
}
