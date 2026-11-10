import { memo, useEffect, useRef, useState, type RefObject } from 'react';
import { TooltipLayer } from './TooltipLayer';
import { PLAIN_TOOLTIP_WIDE, TOOLTIP_DELAY, focusCallsTooltip, justClosed, noteClosed } from './tooltipTrigger';
import { fullText, truncatedAt, truncatedIn } from './truncation';

/**
 * Полный текст обрезанной клетки (sharedui-truncated-cell-tooltip, [UI-52] [UI-53], #1519) — сетка показывает
 * его сама, у `DataGrid` и `EditGrid`: экрану ничего не нужно, и подсказка появляется только у клетки, текст
 * которой на деле не поместился (`truncation.ts`). Вид — простая подсказка `Tooltip` шириной 384 px.
 *
 * <p><b>Один слушатель на сетку, не на клетку</b> (D3): обёртка `Tooltip` вокруг каждой клетки — та же цена,
 * что #1394 уже снимал (выбор строки в списке из тысячи строк — на три секунды дольше). Слушатели нативные, на
 * самой таблице: React-обработчик на `<tbody>` ловил бы и события порталов, открытых из клетки (меню, список,
 * календарь), — по дереву React они всплывают, по DOM нет (А1). Своё состояние — здесь, отдельным компонентом:
 * показ и скрытие не перерисовывают сетку.</p>
 *
 * <p>Ритм — общий с `Tooltip`: 400 мс, сразу после соседней. Таймер ставит движение указателя (`pointermove`),
 * а не `pointerover`: прокрутка колесом под неподвижным курсором шлёт `pointerover` каждой проехавшей клетке, и
 * после остановки открылась бы подсказка у случайной (Д1). Касание и зажатая кнопка (выделяют текст) показа не
 * зовут. Фокус с клавиатуры — у фокусируемого содержимого клетки (ссылка, кнопка), не у строки.</p>
 *
 * <p>Диктору показ не нужен — он и так читает текст клетки целиком: слой скрыт от него (`aria-hidden`), и ни
 * `aria-description`, ни `aria-describedby` клетка не получает. Слой пропускает щелчок (`pointer-events-none`),
 * поэтому строка открывается как прежде; Esc закрывает показ, а не окно под ним.</p>
 */
export const TruncatedTips = memo(function TruncatedTips({ table }: { table: RefObject<HTMLTableElement | null> }) {
  const [tip, setTip] = useState<{ text: string; align: 'start' | 'end' } | null>(null);
  const anchor = useRef<HTMLElement | null>(null);
  const actions = useRef({ hide: () => {}, escape: () => {} });

  useEffect(() => {
    const root = table.current;
    if (!root) return;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let hovered: EventTarget | null = null;
    let focusOwner: EventTarget | null = null;
    // После нажатия и Esc — молчать, пока указатель не уйдёт на другой элемент.
    let blocked = false;

    // Клетка СВОЕЙ сетки: событие из вложенной сетки всплывает и сюда, а показать её клетку — дело её самой (А2).
    const ownCell = (el: EventTarget | null): HTMLElement | null => {
      if (!(el instanceof Element)) return null;
      const cell = el.closest<HTMLElement>('td, th');
      return cell && cell.closest('tbody') === root.tBodies[0] ? cell : null;
    };
    const show = (el: HTMLElement, cell: HTMLElement) => {
      const right = /^(right|end)$/.test(getComputedStyle(cell).textAlign);
      anchor.current = el;
      setTip({ text: fullText(el), align: right ? 'end' : 'start' });
    };
    const hide = () => {
      clearTimeout(timer);
      if (anchor.current) noteClosed();
      anchor.current = null;
      focusOwner = null;
      setTip(null);
    };
    actions.current = { hide, escape: () => { hide(); blocked = true; } };

    const onMove = (e: PointerEvent) => {
      if (e.pointerType === 'touch' || e.target === hovered) return;
      hovered = e.target;
      blocked = false;
      if (anchor.current && e.target instanceof Node && anchor.current.contains(e.target)) return;
      if (anchor.current) hide();
      clearTimeout(timer);
      const cell = ownCell(e.target);
      if (!cell || e.buttons !== 0) return;
      const target = e.target as Element;
      const fire = () => {
        if (hovered !== target || blocked) return;
        const el = truncatedAt(target, cell);
        if (el) show(el, cell);
      };
      if (justClosed()) fire(); else timer = setTimeout(fire, TOOLTIP_DELAY);
    };
    const onOut = (e: PointerEvent) => {
      if (e.relatedTarget instanceof Node && root.contains(e.relatedTarget)) return;
      hovered = null;
      if (focusOwner === null) hide(); else clearTimeout(timer);
    };
    const onDown = () => { hide(); blocked = true; };
    const onFocusIn = (e: FocusEvent) => {
      const target = e.target as HTMLElement;
      // Фокус на самой строке (`<tr tabIndex=0>`) клетки не находит — у строки показа нет.
      const cell = ownCell(target);
      if (!cell || !focusCallsTooltip(target)) return;
      const el = truncatedIn(target, cell);
      if (!el) return;
      if (anchor.current) hide();
      show(el, cell);
      focusOwner = target;
    };
    const onFocusOut = (e: FocusEvent) => { if (e.target === focusOwner) hide(); };

    root.addEventListener('pointermove', onMove);
    root.addEventListener('pointerout', onOut);
    root.addEventListener('pointerdown', onDown);
    root.addEventListener('focusin', onFocusIn);
    root.addEventListener('focusout', onFocusOut);
    return () => {
      clearTimeout(timer);
      root.removeEventListener('pointermove', onMove);
      root.removeEventListener('pointerout', onOut);
      root.removeEventListener('pointerdown', onDown);
      root.removeEventListener('focusin', onFocusIn);
      root.removeEventListener('focusout', onFocusOut);
    };
  }, [table]);

  // Строку под открытым показом перерисовали (данные обновились): якорь, ушедший из документа, оставил бы слой в
  // углу экрана — показ закрывается; текст сменился — показ его перечитывает (А8). Наблюдатель — только пока открыт.
  useEffect(() => {
    const body = table.current?.tBodies[0];
    if (!tip || !body) return;
    const observer = new MutationObserver(() => {
      const el = anchor.current;
      if (!el || !el.isConnected) { actions.current.hide(); return; }
      const text = fullText(el);
      if (text !== tip.text) setTip({ ...tip, text });
    });
    observer.observe(body, { childList: true, subtree: true, characterData: true });
    return () => observer.disconnect();
  }, [table, tip]);

  if (!tip) return null;
  return (
    <TooltipLayer anchor={anchor} side="top" align={tip.align} className={PLAIN_TOOLTIP_WIDE}
      onClose={() => actions.current.hide()} onEscape={() => actions.current.escape()}
      aria-hidden data-truncated-tip="">
      {tip.text}
    </TooltipLayer>
  );
});
