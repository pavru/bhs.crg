import { Icon } from './Icon';
import { ArrowDownward, ArrowUpward, type IconSize } from './icons';
import { Tooltip } from './Tooltip';

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
  size = 18, className = '', upTitle = 'Выше', downTitle = 'Ниже',
}: {
  onUp: () => void;
  onDown: () => void;
  isFirst: boolean;
  isLast: boolean;
  /** Перестановка временно недоступна (например, сохраняется предыдущая) — по той же причине
   *  не `disabled`: фокус нужен пользователю ПОСЛЕ того, как сохранение закончится. */
  busy?: boolean;
  size?: IconSize;
  /** Доп. классы для каждой кнопки (отступы/цвета конкретного редактора). */
  className?: string;
  upTitle?: string;
  downTitle?: string;
}) {
  const cls = `p-0.5 text-fg4 hover:text-on-surface-variant aria-disabled:opacity-25 aria-disabled:hover:text-fg4 ${className}`;
  const upBlocked = isFirst || busy;
  const downBlocked = isLast || busy;
  return (
    <>
      <Tooltip content={upTitle} name>
        <button type="button" onClick={() => { if (!upBlocked) onUp(); }} aria-disabled={upBlocked} className={cls}>
          <Icon icon={ArrowUpward} size={size} />
        </button>
      </Tooltip>
      <Tooltip content={downTitle} name>
        <button type="button" onClick={() => { if (!downBlocked) onDown(); }} aria-disabled={downBlocked} className={cls}>
          <Icon icon={ArrowDownward} size={size} />
        </button>
      </Tooltip>
    </>
  );
}
