import { useEffect, useId, useRef, type FocusEvent, type KeyboardEvent, type ReactElement } from 'react';
import { BASE, HEIGHT, LABEL, padding, variantClass } from './buttonClasses';
import { focusFromLayer } from './layerFocus';
import { TooltipLayer, type TooltipSide } from './TooltipLayer';
import { PLAIN_TOOLTIP, useTooltipTrigger } from './tooltipTrigger';

/**
 * Богатая подсказка MD3 (rich tooltip, [UI-72]) — пояснение с заголовком и до двух действий: «Сначала
 * сохраните разбиение» → «Сохранить». Триггер и слой — общие с `Tooltip`; своим файлом она из-за другого
 * поведения и роли, а не размера.
 *
 * <p><b>Без действия</b> ведёт себя как простая: `role="tooltip"`, сквозь неё щёлкают, уход указателя
 * её закрывает. <b>С действием</b> — немодальный `dialog` с именем по заголовку (`tooltip` с кнопками
 * нарушает ARIA):</p>
 * <ul>
 *   <li>уход указателя закрывает её через 300 мс, если он не перешёл в неё саму;</li>
 *   <li>Tab с триггера переводит фокус на первое действие — портал в `body` в порядке Tab не стоит;
 *       Shift+Tab с первого действия возвращает на триггер, Tab с последнего — дальше по странице;</li>
 *   <li>уход фокуса с триггера в неё её не закрывает, уход из неё мимо триггера — закрывает;</li>
 *   <li>Esc закрывает и возвращает фокус на триггер.</li>
 * </ul>
 *
 * <p>На телефоне наведения нет: действие богатой подсказки MUST стоять и на экране — это решает экран,
 * подсказка лишь не мешает.</p>
 */

/**
 * Действие — text-кнопка MD3 из тех же классов, что `Button`: сам `Button` держит богатую подсказку
 * (`rich`), и импорт его отсюда замкнул бы модули в круг.
 */
const ACTION = `${BASE} touch-target rounded-full ${LABEL} ${HEIGHT} whitespace-nowrap ${padding('text')} ${variantClass('text')}`;

/** Указатель ушёл с триггера или подсказки — столько ждём, не перейдёт ли он на другую из двух. */
const LEAVE_MS = 300;

/** Фокусируемое, по которому идёт Tab: следующее за триггером — когда фокус уходит из подсказки вперёд. */
const TABBABLE = 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

/**
 * Селектор видимости не знает: скрытое (`display: none`, свёрнутая панель) фокус не примет — и он упал
 * бы в `body`. Движок без `checkVisibility` (тестовый DOM) — считаем видимым.
 */
const visible = (el: HTMLElement) => typeof el.checkVisibility !== 'function' || el.checkVisibility({ visibilityProperty: true });

export interface RichTooltipAction {
  label: string;
  onClick: () => void;
}

interface RichTooltipProps {
  title?: string;
  text: string;
  /** До двух text-действий. */
  actions?: readonly RichTooltipAction[];
  /** Один элемент, как у `Tooltip`: неактивная своя кнопка становится `aria-disabled`. */
  children: ReactElement;
  /**
   * Подсказка — ещё и ИМЯ элемента, как у `Tooltip`: кнопка из одной иконки иначе осталась бы без имени.
   * Имя — заголовок (нет его — текст), остальное — описанием.
   */
  name?: boolean;
  side?: TooltipSide;
}

export function RichTooltip({ title, text, actions = [], children, name = false, side = 'bottom' }: RichTooltipProps) {
  const interactive = actions.length > 0;
  // Ни заголовка, ни действия — это простая подсказка и по виду: так кнопка, которой богатую подсказку
  // дают лишь иногда, не пересоздаётся при смене (`Button rich`).
  const plain = !title && !interactive;
  const titleId = useId();
  const layer = useRef<HTMLDivElement>(null);
  const leaving = useRef<ReturnType<typeof setTimeout>>(undefined);
  const cancelLeave = () => clearTimeout(leaving.current);
  useEffect(() => cancelLeave, []);

  const aria: Record<string, string> = !name ? { 'aria-description': title ? `${title}. ${text}` : text }
    : title ? { 'aria-label': title, 'aria-description': text } : { 'aria-label': text };
  const { open, hide, anchor, Element, props } = useTooltipTrigger(children, {
    empty: !text,
    aria,
    onShow: cancelLeave,
    ...(interactive && {
      // Ещё не открылась — уход отменяет её сразу: иначе таймер наведения открыл бы её уже без указателя.
      onLeave: () => { if (open) leaveSoon(); else close(); },
      onBlur: (e: FocusEvent<HTMLElement>) => { if (!inLayer(e.relatedTarget)) close(); },
      onKeyDown: (e: KeyboardEvent<HTMLElement>) => {
        if (e.key !== 'Tab' || e.shiftKey || !open) return;
        const first = layer.current?.querySelector<HTMLElement>('button');
        if (!first) return;
        e.preventDefault();
        first.focus();
      },
    }),
  });

  function close() { cancelLeave(); hide(); }
  function leaveSoon() { cancelLeave(); leaving.current = setTimeout(close, LEAVE_MS); }
  const inLayer = (node: EventTarget | null) => node instanceof Node && !!layer.current?.contains(node);

  /** Закрыть и вернуть фокус на триггер, если он был в подсказке: иначе он ушёл бы в `body`. */
  function closeToTrigger() {
    const had = inLayer(document.activeElement);
    close();
    if (had && anchor.current) focusFromLayer(anchor.current);
  }

  function onLayerKeyDown(e: KeyboardEvent<HTMLDivElement>) {
    if (e.key !== 'Tab') return;
    const buttons = [...(layer.current?.querySelectorAll<HTMLElement>('button') ?? [])];
    const atStart = e.shiftKey && e.target === buttons[0];
    const atEnd = !e.shiftKey && e.target === buttons.at(-1);
    if (!atStart && !atEnd) return;
    e.preventDefault();
    const trigger = anchor.current;
    if (!trigger) return close();
    if (atStart) { trigger.focus(); return; }
    // Вперёд — к следующему за триггером: подсказка закрывается, как закрылась бы, уйди фокус с триггера.
    const all = [...document.querySelectorAll<HTMLElement>(TABBABLE)].filter(el => !inLayer(el) && (el === trigger || visible(el)));
    const next = all[all.indexOf(trigger) + 1];
    close();
    (next ?? trigger).focus();
  }

  return (
    <>
      <Element {...props} />
      {open && text && (
        <TooltipLayer anchor={anchor} side={side} onClose={close} onEscape={closeToTrigger} interactive={interactive}
          layerRef={layer}
          role={interactive ? 'dialog' : 'tooltip'}
          {...(interactive && title ? { 'aria-labelledby': titleId } : interactive ? { 'aria-label': text } : {})}
          onPointerEnter={interactive ? cancelLeave : undefined}
          onPointerLeave={interactive ? leaveSoon : undefined}
          onBlur={interactive ? (e: FocusEvent<HTMLDivElement>) => {
            if (!inLayer(e.relatedTarget) && e.relatedTarget !== anchor.current) close();
          } : undefined}
          onKeyDown={interactive ? onLayerKeyDown : undefined}
          className={plain ? PLAIN_TOOLTIP : `max-w-80 rounded-md bg-surface-container shadow-elevation-2 px-4 pt-3 ${interactive ? 'pb-1' : 'pb-3'}`}>
          {title && <p id={titleId} className="text-title-small text-on-surface">{title}</p>}
          {plain ? text : <p className={`text-body-medium text-on-surface-variant whitespace-pre-line ${title ? 'mt-1' : ''}`}>{text}</p>}
          {interactive && (
            <div className="flex gap-2 -ml-3 mt-1">
              {actions.slice(0, 2).map(a => (
                <button key={a.label} type="button" className={ACTION} onClick={() => { a.onClick(); closeToTrigger(); }}>{a.label}</button>
              ))}
            </div>
          )}
        </TooltipLayer>
      )}
    </>
  );
}
