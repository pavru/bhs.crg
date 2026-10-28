import { AlertCircle } from 'lucide-react';
import { Tooltip } from './Tooltip';

/**
 * Ошибка набранного для скринридера (живая область) и значок с подсказкой — у поля без своей подписи
 * (ячейка, отбор). У поля формы (`onProblem` задан) ошибка стоит под рамкой, значка нет.
 */
export function SegmentProblemMark({ id, problem, showIcon, compact }: { id: string; problem: string | null; showIcon: boolean; compact: boolean }) {
  return (
    <>
      <span id={id} className="sr-only" aria-live="polite">{problem ?? ''}</span>
      {problem && showIcon && (
        <Tooltip content={problem}>
          <span className="ml-1 shrink-0 flex items-center text-error">
            <AlertCircle size={compact ? 13 : 14} aria-hidden />
          </span>
        </Tooltip>
      )}
    </>
  );
}
