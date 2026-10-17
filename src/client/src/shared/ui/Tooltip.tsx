import * as RadixTooltip from '@radix-ui/react-tooltip';
import { Slot, type SlotProps } from '@radix-ui/react-slot';
import { cloneElement, createContext, useContext, useState, type MouseEvent, type ReactElement, type ReactNode } from 'react';

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

/**
 * Неактивная кнопка, которая говорит почему: `aria-disabled`, а не `disabled`. Настоящий `disabled` не
 * получает ни наведения, ни фокуса — причину не увидеть ни мышью, ни с клавиатуры, а обёртка вокруг неё
 * сбивала раскладку и пересоздавала кнопку на каждом переключении (ревью #1396). Такая кнопка остаётся
 * в Tab, скринридер называет её недоступной, нажатие ничего не делает — и форму не отправляет: щелчок
 * отменён.
 *
 * <p>Вид — тот, что кнопке задан для неактивной: `disabled:opacity-40` становится `opacity-40`, а
 * `disabled:pointer-events-none` и подсветка наведения и нажатия уходят — они звали бы нажать то, что
 * не нажимается, или прятали бы от мыши саму подсказку.</p>
 */
interface Disableable { disabled?: boolean; className?: string; 'aria-disabled'?: unknown }

const looksDisabled = (cls: string) => cls.split(/\s+/)
  .filter(c => c && c !== 'disabled:pointer-events-none' && !/^(dark:)?(hover|active):/.test(c))
  .map(c => c.replace(/^disabled:/, ''))
  .join(' ');

function withReason(child: ReactElement): ReactElement {
  const props = child.props as Disableable;
  // Только своя кнопка: `disabled` у триггера меню (`DropdownMenu.Trigger disabled`) — его решение,
  // снятый он открыл бы меню, которое закрыто нарочно.
  if (!props.disabled || typeof child.type !== 'string') return child;
  return cloneElement(child as ReactElement<Disableable & { onClick?: (e: MouseEvent) => void }>, {
    disabled: false,
    'aria-disabled': true,
    className: looksDisabled(props.className ?? ''),
    onClick: (e: MouseEvent) => e.preventDefault(),
  });
}

/**
 * Прокладка между триггером подсказки и ребёнком: снимает `data-state` подсказки. Radix кладёт его на
 * триггер, и внутри триггера меню или всплывающего окна (`<Tooltip><Popover.Trigger asChild>`) он
 * перебивал бы их собственный: `data-[state=open]:` у кнопки гас бы при открытом окне. По состоянию
 * подсказки не оформлено ничего.
 */
function Unstated({ 'data-state': _, ...props }: SlotProps & { 'data-state'?: string }) {
  return <Slot {...props} />;
}

interface TooltipProps {
  /** Текст подсказки; пустой — подсказки нет. */
  content: ReactNode;
  /**
   * Один элемент, умеющий ref и обработчики (кнопка, ссылка, span). Неактивный (`disabled`) с подсказкой
   * становится `aria-disabled` и подсказку показывает — она объясняет, почему нельзя (`withReason`).
   * Триггер меню или всплывающего окна оборачивают целиком — `<Tooltip><Popover.Trigger asChild>`, а
   * не кнопку внутри него: свойства, которые триггер отдаёт ребёнку, сквозь `Tooltip` не проходят.
   */
  children: ReactElement;
  /**
   * Подсказка — ещё и ИМЯ триггера: у кнопки без подписи (одна иконка) браузерный `title` давал его
   * сам. Без этого строковая подсказка ложится постоянным описанием (`aria-description`) — Radix
   * описывает триггер, только пока подсказка открыта.
   */
  name?: boolean;
  side?: 'top' | 'right' | 'bottom' | 'left';
}

export function Tooltip({ content, children, name = false, side = 'top' }: TooltipProps) {
  const shared = useContext(Shared);
  const [open, setOpen] = useState(false);
  const empty = content == null || content === '' || content === false;
  const text = typeof content === 'string' && content !== '' ? content : undefined;
  const own = (children.props as { 'aria-label'?: unknown })['aria-label'];
  const aria = !text ? {} : name ? { 'aria-label': text } : own === text ? {} : { 'aria-description': text };

  // Разметка одна и та же с подсказкой и без: условная подсказка (`cond ? 'почему' : undefined`)
  // иначе пересоздавала бы триггер на каждом переключении — и кнопка теряла бы фокус.
  const root = (
    <RadixTooltip.Root open={open && !empty} onOpenChange={setOpen}>
      {/* Фокус открывает подсказку, только когда он виден — пришёл с клавиатуры. Фокус, который
          вернуло окно (выход с шага к открывшей его кнопке), иначе открыл бы её сам — и первый Esc
          достался бы подсказке, а не окну. */}
      <RadixTooltip.Trigger asChild {...aria} onFocus={e => { if (!focusVisible(e.currentTarget)) e.preventDefault(); }}>
        <Unstated>{empty ? children : withReason(children)}</Unstated>
      </RadixTooltip.Trigger>
      {!empty && (
        <RadixTooltip.Portal>
          <RadixTooltip.Content side={side} sideOffset={6} collisionPadding={8}
            className="z-[60] max-w-60 rounded bg-surface border border-stroke px-2 py-1 text-caption text-fg1 whitespace-pre-line pointer-events-none select-none"
            style={{ boxShadow: 'var(--f-shadow16)' }}>
            {content}
          </RadixTooltip.Content>
        </RadixTooltip.Portal>
      )}
    </RadixTooltip.Root>
  );
  // Без общего провайдера (тест компонента, экран вне App) — свой: Radix без него падает.
  return shared ? root : <TooltipProvider>{root}</TooltipProvider>;
}
