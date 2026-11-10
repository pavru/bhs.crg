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
 * `aria-description`, ни `aria-describedby` клетка не получает. Слой пропускает указатель (`TooltipLayer`, и в
 * окне тоже), поэтому строка под ним открывается как прежде; Esc закрывает показ, а не окно под ним.</p>
 */
export const TruncatedTips = memo(function TruncatedTips({ table }: { table: RefObject<HTMLTableElement | null> }) {
  const [tip, setTip] = useState<{ text: string; align: 'start' | 'end' } | null>(null);
  const anchor = useRef<HTMLElement | null>(null);
  const actions = useRef({ hide: () => {}, escape: () => {} });

  useEffect(() => {
    const root = table.current;
    if (!root) return;
    let timer: ReturnType<typeof setTimeout> | undefined;
    // Элемент, у которого ждут показа (таймер) или он открыт либо уже измерен: повторное движение по нему —
    // ничего. Сбрасывается всем, что ожидание отменяет, — иначе показ у него не вернулся бы до ухода на другой.
    let hovered: Element | null = null;
    let focusOwner: EventTarget | null = null;
    // После нажатия и Esc — молчать, пока указатель не уйдёт из этой клетки: переход на вложенный `<b>` или
    // значок того же текста — не уход, и закрытый показ не открывается снова.
    let blockedIn: Element | null = null;

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
      hovered = null;
      focusOwner = null;
      setTip(null);
    };
    const block = (el: EventTarget | null) => {
      blockedIn = el instanceof Element ? el.closest('td, th') ?? el : null;
    };
    actions.current = {
      hide,
      escape: () => { block(anchor.current); hide(); },
    };

    const onMove = (e: PointerEvent) => {
      if (e.pointerType === 'touch' || !(e.target instanceof Element) || e.target === hovered) return;
      const target = e.target;
      if (blockedIn && !blockedIn.contains(target)) blockedIn = null;
      if (anchor.current?.contains(target)) { hovered = target; return; }
      if (anchor.current) hide();
      clearTimeout(timer);
      hovered = null;
      const cell = ownCell(target);
      if (!cell || e.buttons !== 0 || blockedIn) return;
      hovered = target;
      const fire = () => {
        if (hovered !== target) return;
        const el = truncatedAt(target, cell);
        if (el) show(el, cell);
      };
      if (justClosed()) fire(); else timer = setTimeout(fire, TOOLTIP_DELAY);
    };
    const onOut = (e: PointerEvent) => {
      if (e.relatedTarget instanceof Node && root.contains(e.relatedTarget)) return;
      if (focusOwner === null) hide(); else { clearTimeout(timer); hovered = null; }
    };
    const onDown = (e: PointerEvent) => { hide(); block(e.target); };
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

  // Строку под открытым показом перерисовали (данные обновились, колонку сузили или расширили): якорь, ушедший из
  // документа, оставил бы слой в углу экрана — показ закрывается; текст больше не обрезан (стал короче, снят
  // `truncate`) — тоже: показ только у обрезанного ([UI-53]); текст сменился — показ его перечитывает (А8).
  // Наблюдатель — за всей таблицей (шапка меняет ширины) и только пока показ открыт.
  useEffect(() => {
    const root = table.current;
    if (!tip || !root) return;
    const observer = new MutationObserver(() => {
      const el = anchor.current;
      const cell = el?.closest<HTMLElement>('td, th');
      if (!el || !el.isConnected || !cell || truncatedAt(el, cell) !== el) { actions.current.hide(); return; }
      const text = fullText(el);
      if (text !== tip.text) setTip({ ...tip, text });
    });
    observer.observe(root, {
      childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ['class', 'style'],
    });
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
