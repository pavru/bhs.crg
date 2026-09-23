/**
 * Сворачиваемая секция «Заполняются автоматически».
 *
 * <p>Выделено из `ComplexFields.tsx` (issue #1014). Самостоятельна: принимает счётчики и детей,
 * ни одного редактора полей не зовёт.</p>
 */
import { useState, type ReactNode } from 'react';
import { ChevronDown, ChevronUp, Database } from 'lucide-react';
import { LOCKED_HINT } from './lockedFields';

/** Сворачиваемая секция «Заполняются автоматически» (issue #102, P2): read-only поля из источника
 *  прячем по умолчанию, чтобы длинная форма не выглядела «портянкой» одинаковых боксов. */
export function AutoFieldsSection(
  { count, recognizedCount = 0, staleCount = 0, lockedCount = 0, staleHint, children }:
  { count: number; recognizedCount?: number; staleCount?: number; lockedCount?: number;
    staleHint?: string; children: ReactNode },
) {
  const [open, setOpen] = useState(false);
  return (
    <div className="border border-dashed border-stroke rounded-lg overflow-hidden">
      <button type="button" onClick={() => setOpen(v => !v)} aria-expanded={open}
        className="w-full flex items-center gap-2 px-3 py-2 bg-base/40 hover:bg-base transition-colors text-left">
        {open ? <ChevronUp size={12} className="text-fg4 shrink-0" /> : <ChevronDown size={12} className="text-fg4 shrink-0" />}
        {/* Глиф называет НАЗНАЧЕНИЕ секции, а не её содержимое, и продублирован подписью рядом —
            поэтому он приглушён: brand в этой строке должен быть ровно один, и указывать он должен
            на факт, ради которого сюда смотрят. */}
        <Database size={11} className="text-fg4 shrink-0" />
        <span className="text-xs text-fg3 flex-1">Заполняются автоматически</span>
        {/* Факт в подписи самой раскрывашки — здесь он НЕСУЩИЙ, а не дублирующий: секция свёрнута
            по умолчанию, и значок у поля внутри неё человек по умолчанию не видит вовсе. Отдельной
            интерактивности факту не даём — заголовок и так кнопка с очевидным действием.
            Общее число оставлено намеренно: «со сканов: 4» — подмножество, и без основания оно не
            читается. 4 из 4 и 4 из 40 — разные решения, а решает именно отношение. */}
        <span className="text-xs text-fg4">
          {count} п.
          {recognizedCount > 0 && (
            <span className="text-brand"
              title="Часть значений распознана с отсканированных документов — возможны ошибки чтения. Править их можно в источнике данных.">
              {' · '}
              {/* Совпали — говорим словом: сравнивать два числа на глаз читателю не должно
                  приходиться (в прошлый раз этот дефект пришлось записать в «не чиним»). */}
              {recognizedCount === count ? 'все со сканов' : `со сканов: ${recognizedCount}`}
            </span>
          )}
          {/* Устаревание — ОТДЕЛЬНОЕ слагаемое, а не оттенок предыдущего (issue #815): устареть
              может и обычный источник, не разобравшийся против нового файла, так что «со сканов: 0 ·
              устарело: 2» — законная строка. Цвет warning: это оговорка к данным, а не поломка. */}
          {staleCount > 0 && (
            <span className="text-warning" title={staleHint}>
              {' · '}устарело: {staleCount}
            </span>
          )}
          {/* Поля с замком (ТЗ CORE-20.2) — ТРЕТЬЕ слагаемое, а не оттенок первых двух: их кладёт
              код модуля, а не источник данных, и «со сканов» про них неверно. Без этой строки
              свёрнутая секция говорила бы только «8 п.», и человек, ищущий поле, которое вдруг
              стало нередактируемым, не узнал бы причину, не раскрыв её. */}
          {lockedCount > 0 && (
            <span className="text-fg4" title={LOCKED_HINT}>
              {' · '}
              {lockedCount === count ? 'все от модуля' : `от модуля: ${lockedCount}`}
            </span>
          )}
        </span>
      </button>
      {open && <div className="px-3 py-3 border-t border-stroke">{children}</div>}
    </div>
  );
}
