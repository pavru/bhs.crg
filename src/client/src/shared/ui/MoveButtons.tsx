import { ArrowUp, ArrowDown } from 'lucide-react';

/**
 * Пара кнопок «Выше»/«Ниже» для списков с перестановкой (issue #542).
 *
 * Существует ради одной детали, на которой независимо спотыкались шесть редакторов: на краю списка
 * кнопку НЕЛЬЗЯ делать `disabled`. Браузер снимает фокус с элемента, ставшего выключенным, поэтому
 * пользователь, доводящий строку клавиатурой до края, оказывается на `<body>` и теряет место —
 * последний шаг ровно того сценария, ради которого чинился #517.
 *
 * Поэтому `aria-disabled`: кнопка остаётся фокусируемой, для скринридера по-прежнему выключена, а
 * край проверяется в обработчике. Внешне — то же приглушение.
 */
export function MoveButtons({
  onUp, onDown, isFirst, isLast, busy = false,
  size = 12, className = '', upTitle = 'Выше', downTitle = 'Ниже',
}: {
  onUp: () => void;
  onDown: () => void;
  isFirst: boolean;
  isLast: boolean;
  /** Перестановка временно недоступна (например, сохраняется предыдущая) — по той же причине
   *  не `disabled`: фокус нужен пользователю ПОСЛЕ того, как сохранение закончится. */
  busy?: boolean;
  size?: number;
  /** Доп. классы для каждой кнопки (отступы/цвета конкретного редактора). */
  className?: string;
  upTitle?: string;
  downTitle?: string;
}) {
  const cls = `p-0.5 text-fg4 hover:text-fg2 aria-disabled:opacity-25 aria-disabled:hover:text-fg4 ${className}`;
  const upBlocked = isFirst || busy;
  const downBlocked = isLast || busy;
  return (
    <>
      <button type="button" onClick={() => { if (!upBlocked) onUp(); }} aria-disabled={upBlocked}
        title={upTitle} className={cls}>
        <ArrowUp size={size} />
      </button>
      <button type="button" onClick={() => { if (!downBlocked) onDown(); }} aria-disabled={downBlocked}
        title={downTitle} className={cls}>
        <ArrowDown size={size} />
      </button>
    </>
  );
}
