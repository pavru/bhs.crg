import { useCallback, useEffect, useState, type RefObject } from 'react';
import { loupeAvailable } from './magnifierGeometry';
import { isEditable } from './viewerInput';

/**
 * Состояние лупы просмотрщика — общее для ленты PDF и картинки (issue #1871, [UI-92], [UI-94]).
 *
 * - Новый ключ документа — лупа выключена. От масштаба листа 300 % она недоступна: включённая
 *   выключается, кнопка неактивна (кратность там ×1,33 и меньше, выбор владельца 11.11.2026).
 * - **Esc — слушателем `window` в фазе перехвата**, пока фокус в просмотрщике или указатель над ним.
 *   Окно поверх экрана (`Modal`) ловит Esc на документе тоже перехватом; слушатель окна браузера идёт
 *   раньше, и первый Esc выключает лупу, а не закрывает окно — даже когда фокус в его шапке. Esc поля
 *   (номер страницы), открытого меню и диалога, открытого поверх просмотрщика, остаётся им: указатель
 *   мог так и остаться над листом, а фокус уже в подтверждении.
 * - Без точного указателя (`any-pointer: fine` ложно — только сенсор) лупы нет вовсе: `null`. Именно
 *   `any-pointer`, а не принятое в проекте `pointer: coarse`: перо и мышь у планшета дают лупу. Мышь
 *   отключили при включённой лупе — лупа выключена: невидимая лупа не должна забирать Esc, а вернувшаяся
 *   мышь — включать её сама.
 */

export interface Loupe {
  on: boolean;
  available: boolean;
  toggle: () => void;
}

const FINE = '(any-pointer: fine)';

/** Есть ли у устройства точный указатель. Нет `matchMedia` (среда тестов) — считаем, что есть. */
function useFinePointer(): boolean {
  const media = () => (typeof window.matchMedia === 'function' ? window.matchMedia(FINE) : null);
  const [fine, setFine] = useState(() => media()?.matches ?? true);
  useEffect(() => {
    const query = media();
    if (!query) return;
    const update = () => setFine(query.matches);
    query.addEventListener('change', update);
    return () => query.removeEventListener('change', update);
  }, []);
  return fine;
}

/** Фокус в слое поверх просмотрщика, у которого Esc свой: меню масштаба, список. */
const inLayer = (el: Element | null) => !!el?.closest('[role="menu"], [role="listbox"]');

/** Фокус в диалоге, который просмотрщик не содержит: открытом поверх него, а не его собственном окне. */
function inOtherDialog(active: Element | null, root: HTMLElement): boolean {
  const dialog = active?.closest('[role="dialog"], [role="alertdialog"]');
  return !!dialog && !dialog.contains(root);
}

export function useLoupe({ documentKey, scale, root, hovered, setAnnounce }: {
  documentKey: string;
  scale: number;
  root: RefObject<HTMLElement | null>;
  /** Указатель над просмотрщиком — флаг `useZoomInput`: для Esc при фокусе снаружи. */
  hovered: RefObject<boolean>;
  setAnnounce: (text: string) => void;
}): Loupe | null {
  const fine = useFinePointer();
  const [on, setOn] = useState(false);
  const [shownKey, setShownKey] = useState(documentKey);
  const available = loupeAvailable(scale);

  // Сброс прямо при отрисовке, как у остального показа: эффектом лупа мелькнула бы на новом документе.
  if (documentKey !== shownKey) {
    setShownKey(documentKey);
    setOn(false);
  }
  if (on && (!available || !fine)) setOn(false);

  const toggle = useCallback(() => {
    setAnnounce(on ? 'Лупа выключена' : 'Лупа включена');
    setOn(!on);
  }, [on, setAnnounce]);

  useEffect(() => {
    if (!on) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape' || e.defaultPrevented) return;
      const active = document.activeElement, el = root.current;
      if (!el || isEditable(active) || inLayer(active) || inOtherDialog(active, el)) return;
      if (!el.contains(active) && !hovered.current) return;
      e.preventDefault();
      e.stopPropagation();
      setOn(false);
      setAnnounce('Лупа выключена');
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [on, root, hovered, setAnnounce]);

  if (!fine) return null;
  return { on: on && available, available, toggle };
}
