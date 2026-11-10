import { useCallback, useEffect, useRef, useState, type RefObject } from 'react';
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
 *   (номер страницы) и открытого меню остаётся им.
 * - Без точного указателя (`any-pointer: fine` ложно — только сенсор) лупы нет вовсе: `null`. Именно
 *   `any-pointer`, а не принятое в проекте `pointer: coarse`: перо и мышь у планшета дают лупу.
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

export function useLoupe({ documentKey, scale, root, setAnnounce }: {
  documentKey: string;
  scale: number;
  root: RefObject<HTMLElement | null>;
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
  if (on && !available) setOn(false);

  const toggle = useCallback(() => {
    setAnnounce(on ? 'Лупа выключена' : 'Лупа включена');
    setOn(!on);
  }, [on, setAnnounce]);

  // Указатель над просмотрщиком — для Esc при фокусе снаружи.
  const hovered = useRef(false);
  useEffect(() => {
    const el = root.current;
    if (!el) return;
    const enter = () => { hovered.current = true; };
    const leave = () => { hovered.current = false; };
    el.addEventListener('pointerover', enter);
    el.addEventListener('pointerleave', leave);
    return () => {
      el.removeEventListener('pointerover', enter);
      el.removeEventListener('pointerleave', leave);
    };
  }, [root]);

  useEffect(() => {
    if (!on) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape' || e.defaultPrevented) return;
      const active = document.activeElement;
      if (isEditable(active) || inLayer(active)) return;
      const inside = !!root.current && (root.current.contains(active) || hovered.current);
      if (!inside) return;
      e.preventDefault();
      e.stopPropagation();
      setOn(false);
      setAnnounce('Лупа выключена');
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [on, root, setAnnounce]);

  if (!fine) return null;
  return { on: on && available, available, toggle };
}
