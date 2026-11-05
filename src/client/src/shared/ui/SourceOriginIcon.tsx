import { Icon } from './Icon';
import { Database, DocumentScanner } from './icons';
import type { DataOrigin, DataSetStaleReason } from '@/shared/api/types';
import { Tooltip } from './Tooltip';
import { sourceOriginText } from './sourceOriginText';

/**
 * Значок «откуда это значение» у поля, заполняемого источником данных.
 *
 * Значок ОДИН и меняет форму, а не добавляется рядом: у поля и так конкурируют метки о поломках
 * (нерешённые ссылки, нарушенные ограничения, обязательность), и шестая отняла бы у них внимание.
 *
 * У него две НЕЗАВИСИМЫЕ оси (issue #815):
 *   форма — происхождение (распознано со скана / подставляется из источника),
 *   цвет  — состояние (норма / данные разошлись с файлом).
 * Независимость здесь не украшение: устареть может и парсерный источник, у которого происхождение
 * обычное, — свяжи цвет с формой, и такое поле промолчало бы.
 *
 * Происхождение показывается только распознанное: у `Parsed` и `System` нет действия для читателя,
 * а метка без действия превращается в фон. Устаревание показывается у любого.
 *
 * У привязанного скалярного поля значка нет: его оси несёт кнопка привязки в хвосте поля ([UI-86]).
 * `id` — у пояснения для диктора: поле ссылается на него через `aria-describedby`.
 */
export function SourceOriginIcon(
  { origin, plural, stale, staleReason, id }:
  { origin?: DataOrigin; plural?: boolean; stale?: boolean; staleReason?: DataSetStaleReason | null; id?: string },
) {
  const recognized = origin === 'Recognized';
  const title = sourceOriginText({ recognized, plural, stale, staleReason });
  const cls = stale ? 'text-warning' : 'text-primary';

  return (
    <Tooltip content={title}>
      <span>
        {recognized
          ? <Icon icon={DocumentScanner} size={18} className={cls} aria-hidden />
          : <Icon icon={Database} size={18} className={cls} aria-hidden />}
        {/* Подсказка на span доступна только мышью — тем, кто ведёт форму с клавиатуры, признак
            обязан достаться текстом (проект keyboard-first, issue #107). */}
        <span id={id} className="sr-only">{title}</span>
      </span>
    </Tooltip>
  );
}
