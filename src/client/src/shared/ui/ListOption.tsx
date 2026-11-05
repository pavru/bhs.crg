import type { MouseEvent, ReactNode } from 'react';
import { IconButton } from './Button';
import { Icon } from './Icon';
import { Check } from './icons';
import type { IconDef } from './icons/types';
import { ListRowBody, listRowBox, present, type ListRowContent, type ListRowForm } from './listRow';

export type ListOptionProps = ListRowContent & {
  /** Адрес варианта — его называет `aria-activedescendant` поля поиска. */
  id: string;
  /** Вид по контейнеру (П1 = В): окно выбора — `list` (как `ListItem`), слой под полем — `menu` (как пункт
   *  плотного `Menu`). */
  form?: ListRowForm;
  /** Активный вариант — его выберет Enter в поле поиска: заливка `secondary-container`. */
  active: boolean;
  /** Текущее значение поля: галочка у замыкающего края (П2) и `aria-selected`. */
  current?: boolean;
  /** Архивный вариант: в списке, с пометкой «в архиве», не выбирается. */
  disabled?: boolean;
  /** Надпись над подписью (`label-small` роли `primary`) — «Лучшее совпадение». Только в форме `list`. */
  overline?: ReactNode;
  onChoose: () => void;
  /** Указатель сдвинулся над неактивным вариантом — активным становится он. */
  onHover?: () => void;
  /** Действие над вариантом, отличное от выбора (шаг вглубь), — отдельная цель у замыкающего края. Фокус оно
   *  не берёт и вариант не выбирает. */
  action?: { icon: IconDef; label: string; onAct: () => void };
  /** Пояснение для диктора (`aria-description`): что ещё умеет вариант («стрелка вправо — разделы»). */
  description?: string;
  /** Уровень в дереве (0 — корень). */
  depth?: number;
  'data-testid'?: string;
};

// Поле не теряет фокус: иначе слой под полем закрылся бы раньше щелчка, а стрелки перестали бы ходить.
const keepFocus = (e: MouseEvent) => e.preventDefault();

/**
 * Вариант выбора в списке под полем поиска ([UI-87], sharedui-md3-21): `role="option"` listbox'а, фокус которого
 * живёт в поле (`aria-activedescendant`). Сам вариант фокуса не берёт и остановкой Tab не бывает.
 *
 * <p>Подсвеченным бывает только активный (`secondary-container`), наведение — слоем состояния. Лучшее совпадение,
 * текущее значение и архив — пометками в строке, своей заливки у них нет. `aria-selected` — у текущего значения,
 * а не у активного: активного называет поле.</p>
 */
export function ListOption({ id, form = 'list', active, current = false, disabled = false, overline, onChoose, onHover,
  action, description, depth = 0, trailing, 'data-testid': testId, ...content }: ListOptionProps) {
  const twoLine = form === 'list' && (present(content.supporting) || present(overline));
  const box = listRowBox({ form, twoLine, lead: false, tail: !!action, depth });
  const tail = (
    <>
      {disabled && <span>в архиве</span>}
      {trailing}
      {current && <><Icon icon={Check} size={24} /><span className="sr-only">сейчас</span></>}
    </>
  );
  const marked = disabled || present(trailing) || current;
  const fill = active ? 'bg-secondary-container text-on-secondary-container' : 'text-on-surface';
  return (
    <div id={id} role="option" aria-selected={current} aria-disabled={disabled || undefined} aria-description={description}
      data-testid={testId} onMouseDown={keepFocus} onClick={disabled ? undefined : onChoose}
      onMouseMove={onHover && !active ? onHover : undefined}
      className={`flex items-stretch w-full select-none ${disabled ? 'disabled-content' : 'cursor-pointer state-layer'} ${fill}`}>
      <div className={box}>
        <ListRowBody form={form} filled={active} overline={form === 'list' ? overline : undefined}
          trailing={marked ? tail : undefined} {...content} />
      </div>
      {action && (
        <span className="shrink-0 flex items-center pr-1" aria-hidden>
          <IconButton icon={action.icon} label={action.label} tabIndex={-1} onMouseDown={keepFocus}
            onClick={e => { e.stopPropagation(); action.onAct(); }} />
        </span>
      )}
    </div>
  );
}
