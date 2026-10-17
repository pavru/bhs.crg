import * as RadixTooltip from '@radix-ui/react-tooltip';
import { createContext, useContext, type ReactElement, type ReactNode } from 'react';

/**
 * Всплывающая подсказка в нашем стиле вместо нативного `title` (issue #1394, docs/UI.md).
 *
 * <p>Показывается при наведении и при фокусе с клавиатуры — нативный `title` фокуса не замечает;
 * Esc закрывает её, а не окно под ней. На телефоне наведения нет: в подсказке не должно быть ничего,
 * без чего нельзя работать.</p>
 *
 * <p>Вид — на токенах всплывающего слоя: поверхность, текст `caption`, радиус 4 px, тень `shadow16`.
 * Сквозь подсказку щелчок проходит (`pointer-events-none`), а наведение на неё не держит её открытой:
 * иначе щелчок по ней, лежащей вне окна, окно прочло бы как промах мимо него.</p>
 */

/** Есть ли общий провайдер: с ним соседние подсказки открываются без повторной задержки. */
const Shared = createContext(false);

const DELAY = 400;

/** Виден ли фокус. Движок без `:focus-visible` (старый браузер, тестовый DOM) — считаем видимым. */
function focusVisible(el: Element): boolean {
  try { return el.matches(':focus-visible'); }
  catch { return true; }
}

/** Общий провайдер подсказок — один на приложение (`App.tsx`). */
export function TooltipProvider({ children }: { children: ReactNode }) {
  return (
    <RadixTooltip.Provider delayDuration={DELAY} skipDelayDuration={300} disableHoverableContent>
      <Shared.Provider value>{children}</Shared.Provider>
    </RadixTooltip.Provider>
  );
}

interface TooltipProps {
  /** Текст подсказки; пустой — подсказки нет, триггер рисуется как есть. */
  content: ReactNode;
  /** Один элемент, умеющий ref и обработчики (кнопка, ссылка, span). */
  children: ReactElement;
  side?: 'top' | 'right' | 'bottom' | 'left';
  /**
   * Триггер неактивен. Неактивная кнопка не получает ни наведения, ни фокуса, а объясняет подсказка
   * как раз её — почему нельзя. Тогда подсказка вешается на обёртку, и до обёртки доходят и мышь, и Tab.
   */
  disabled?: boolean;
  /** Классы обёртки неактивного триггера — растянуть её, если кнопка растянута (`w-full`). */
  wrapperClassName?: string;
}

export function Tooltip({ content, children, side = 'top', disabled = false, wrapperClassName = '' }: TooltipProps) {
  const shared = useContext(Shared);
  if (content == null || content === '' || content === false) return children;

  const root = (
    <RadixTooltip.Root>
      {/* Фокус открывает подсказку, только когда он виден — пришёл с клавиатуры. Фокус, который
          вернуло окно (выход с шага к открывшей его кнопке), иначе открыл бы её сам — и первый Esc
          достался бы подсказке, а не окну. */}
      <RadixTooltip.Trigger asChild onFocus={e => { if (!focusVisible(e.currentTarget)) e.preventDefault(); }}>
        {disabled
          ? <span tabIndex={0} className={`inline-flex rounded-full focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand ${wrapperClassName}`}>{children}</span>
          : children}
      </RadixTooltip.Trigger>
      <RadixTooltip.Portal>
        <RadixTooltip.Content side={side} sideOffset={6} collisionPadding={8}
          className="z-[60] max-w-60 rounded bg-surface border border-stroke px-2 py-1 text-caption text-fg1 pointer-events-none select-none"
          style={{ boxShadow: 'var(--f-shadow16)' }}>
          {content}
        </RadixTooltip.Content>
      </RadixTooltip.Portal>
    </RadixTooltip.Root>
  );
  // Без общего провайдера (тест компонента, экран вне App) — свой: Radix без него падает.
  return shared ? root : <TooltipProvider>{root}</TooltipProvider>;
}
