import { useState, type ReactElement, type ReactNode } from 'react';
import { TooltipLayer, type TooltipSide } from './TooltipLayer';
import { PLAIN_TOOLTIP, useTooltipTrigger } from './tooltipTrigger';

/**
 * Простая подсказка MD3 (plain tooltip, [UI-72]) вместо нативного `title` (issue #1394, docs/UI.md).
 *
 * <p>Показывается при наведении и при фокусе с клавиатуры — нативный `title` фокуса не замечает;
 * Esc закрывает её, а не окно под ней. На телефоне наведения нет: в подсказке не должно быть ничего,
 * без чего нельзя работать. Заголовок или действие — это `RichTooltip`.</p>
 *
 * <p>Вид — `inverse-surface`, текст `body-small`, форма 4 px, без рамки и тени. Сквозь подсказку щелчок
 * проходит (`pointer-events-none`), а наведение на неё не держит её открытой: иначе щелчок по ней,
 * лежащей вне окна, окно прочло бы как промах мимо него.</p>
 *
 * <p>Триггер и слой — общие с `RichTooltip`: `tooltipTrigger.ts` и `TooltipLayer.tsx`.</p>
 */

interface TooltipProps {
  /** Текст подсказки; пустой — подсказки нет. */
  content: ReactNode;
  /**
   * Один элемент, умеющий ref и обработчики (кнопка, ссылка, span, триггер меню). Неактивная своя
   * кнопка (`<button disabled>`) с подсказкой становится `aria-disabled` и подсказку показывает — она
   * объясняет, почему нельзя. Триггер меню или всплывающего окна оборачивают целиком:
   * `<Tooltip><Popover.Trigger asChild>`, а не кнопку внутри него.
   */
  children: ReactElement;
  /**
   * Подсказка — ещё и ИМЯ элемента: у кнопки без подписи (одна иконка) браузерный `title` давал его
   * сам. Без этого строковая подсказка ложится постоянным описанием (`aria-description`) — кроме случая,
   * когда она повторяет подпись или значение элемента: поле только для чтения с обрезанным текстом
   * показывает его подсказкой целиком, а читалка произнесла бы его дважды.
   */
  name?: boolean;
  side?: TooltipSide;
  /**
   * Открыть ещё и извне — сверх наведения и фокуса самого элемента. Для знака, который фокуса не получает, а
   * объяснить его нужно и клавиатуре, и касанию: знак «со скана» показывает подсказку, пока фокус в его поле
   * ([UI-88]). Esc закрывает её до следующего открытия извне.
   */
  open?: boolean;
}

export function Tooltip({ content, children, name = false, side = 'top', open: outside = false }: TooltipProps) {
  const empty = content == null || content === '' || content === false;
  const text = typeof content === 'string' && content !== '' ? content : undefined;
  const own = children.props as { 'aria-label'?: unknown; value?: unknown };
  const repeats = own['aria-label'] === text || own.value === text;
  const aria: Record<string, string> = !text ? {} : name ? { 'aria-label': text } : repeats ? {} : { 'aria-description': text };
  const { open, hide, anchor, Element, props } = useTooltipTrigger(children, { empty, aria });
  // Закрытая Esc'ом остаётся закрытой, пока зов извне не кончится: иначе Esc её не закрывал бы вовсе.
  const [dismissed, setDismissed] = useState(false);
  const [called, setCalled] = useState(outside);
  if (called !== outside) { setCalled(outside); setDismissed(false); }
  const shown = open || (outside && !dismissed);

  return (
    <>
      <Element {...props} />
      {shown && !empty && (
        <TooltipLayer anchor={anchor} side={side} onClose={hide} onEscape={() => { hide(); setDismissed(true); }}
          role="tooltip" className={PLAIN_TOOLTIP}>
          {content}
        </TooltipLayer>
      )}
    </>
  );
}
