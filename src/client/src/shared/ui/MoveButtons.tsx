import { IconButton } from './Button';
import { ArrowDownward, ArrowUpward } from './icons';

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
 *
 * Кнопки — `IconButton` ([UI-46]): 40, в клетке таблицы — по размеру места ([UI-68]).
 */
export function MoveButtons({
  onUp, onDown, isFirst, isLast, busy = false,
  upTitle = 'Выше', downTitle = 'Ниже',
}: {
  onUp: () => void;
  onDown: () => void;
  isFirst: boolean;
  isLast: boolean;
  /** Перестановка временно недоступна (например, сохраняется предыдущая) — по той же причине
   *  не `disabled`: фокус нужен пользователю ПОСЛЕ того, как сохранение закончится. */
  busy?: boolean;
  upTitle?: string;
  downTitle?: string;
}) {
  const upBlocked = isFirst || busy;
  const downBlocked = isLast || busy;
  return (
    <>
      <IconButton label={upTitle} icon={ArrowUpward} aria-disabled={upBlocked}
        onClick={() => { if (!upBlocked) onUp(); }} />
      <IconButton label={downTitle} icon={ArrowDownward} aria-disabled={downBlocked}
        onClick={() => { if (!downBlocked) onDown(); }} />
    </>
  );
}
