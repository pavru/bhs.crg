import type { ReactElement, ReactNode } from 'react';
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
   * сам. Без этого строковая подсказка ложится постоянным описанием (`aria-description`).
   */
  name?: boolean;
  side?: TooltipSide;
}

export function Tooltip({ content, children, name = false, side = 'top' }: TooltipProps) {
  const empty = content == null || content === '' || content === false;
  const text = typeof content === 'string' && content !== '' ? content : undefined;
  const ownLabel = (children.props as { 'aria-label'?: unknown })['aria-label'];
  const aria: Record<string, string> = !text ? {} : name ? { 'aria-label': text } : ownLabel === text ? {} : { 'aria-description': text };
  const { open, hide, anchor, Element, props } = useTooltipTrigger(children, { empty, aria });

  return (
    <>
      <Element {...props} />
      {open && !empty && (
        <TooltipLayer anchor={anchor} side={side} onClose={hide} role="tooltip" className={PLAIN_TOOLTIP}>
          {content}
        </TooltipLayer>
      )}
    </>
  );
}
