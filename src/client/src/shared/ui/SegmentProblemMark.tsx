import { FieldErrorMark } from './OutlinedField';

/**
 * Ошибка набранного для скринридера (живая область) и знак с подсказкой — у поля без своей подписи
 * (ячейка, отбор). У поля формы (`onProblem` задан) ошибка стоит под рамкой, а знак ставит оболочка:
 * здесь его нет. Знак — тот же `FieldErrorMark` плотного поля ([UI-69]), а не свой: двух видов знака
 * ошибки у полей не бывает.
 */
export function SegmentProblemMark({ id, problem, showIcon }: { id: string; problem: string | null; showIcon: boolean }) {
  return (
    <>
      <span id={id} className="sr-only" aria-live="polite">{problem ?? ''}</span>
      {problem && showIcon && <span className="ml-1 flex"><FieldErrorMark density="dense" message={problem} /></span>}
    </>
  );
}
