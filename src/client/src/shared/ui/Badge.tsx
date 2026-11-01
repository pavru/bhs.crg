/**
 * Badge MD3 ([UI-74]): точка непрочитанного или число на знаке, пункте навигации и вкладке.
 *
 * - small — без `count`: точка 6 px;
 * - large — с `count`: высота 16 px, `label-small`, больше 999 — «999+».
 *
 * <p>Цвет — только `error` / `on-error`, и у непрочитанного без ошибки тоже: у badge MD3 один цвет, а тон
 * по смыслу говорит пометка (`StatusText`). Число у подписи — не badge, а `CountText`.</p>
 *
 * <p>Видимое «999+» скрыто от диктора, а точное число стоит рядом невидимым текстом — так оно входит в
 * доступное имя элемента, к которому badge прикреплён. Имя, заданное `aria-label`, текст не читает: такое
 * имя называет число само. Место на знаке — классами вызывающего (`absolute …`).</p>
 */

/** Больше этого — «999+». */
const BADGE_MAX = 999;

export function Badge({ count, label, className = '' }: {
  count?: number;
  /** Что читает диктор вместо голого числа («устарели: 3»); по умолчанию — само число. */
  label?: string;
  className?: string;
}) {
  if (count === undefined) return <span aria-hidden className={`size-1.5 rounded-full bg-error ${className}`} />;
  return (
    <span className={`inline-grid place-items-center h-4 min-w-4 px-1 rounded-full bg-error text-on-error text-label-small tabular-nums ${className}`}>
      <span aria-hidden>{count > BADGE_MAX ? `${BADGE_MAX}+` : count}</span>
      <span className="sr-only">{label ?? count}</span>
    </span>
  );
}
