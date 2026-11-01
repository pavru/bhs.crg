import { useCallback, useEffect, useMemo, useRef, useState, type RefObject } from 'react';
import type { WindowStepHost, WindowStepSlots, WindowStepState } from './modalControl';

type StepEntry = WindowStepState & { id: string; back: () => void; touched: boolean };

/**
 * Стопка шагов окна (issue #1384): верхний шаг виден, нижние и форма окна смонтированы и скрыты.
 * Под каждый шаг окно заводит свои места — тело и подвал, — и шаг рисует туда порталом: так его
 * состояние живёт там, где он отрисован, и шаг на шаге не стирает нижний.
 */
export function useWindowSteps(content: RefObject<HTMLDivElement | null>) {
  const [steps, setSteps] = useState<StepEntry[]>([]);
  const [slots, setSlots] = useState<Record<string, WindowStepSlots>>({});
  // Откуда ушли на шаг — туда фокус и вернётся: кнопка, открывшая шаг, снова видна.
  const openers = useRef(new Map<string, HTMLElement | null>());
  const restore = useRef<HTMLElement | null>(null);

  const push = useCallback((id: string, back: () => void) => {
    openers.current.set(id, document.activeElement as HTMLElement | null);
    setSteps(s => s.some(x => x.id === id) ? s : [...s, { id, back, title: '', busy: false, touched: false }]);
  }, []);
  const update = useCallback((id: string, next: WindowStepState) => {
    setSteps(s => s.map(x => x.id === id ? { ...x, ...next } : x));
  }, []);
  const pop = useCallback((id: string) => {
    restore.current = openers.current.get(id) ?? null;
    openers.current.delete(id);
    setSteps(s => s.filter(x => x.id !== id));
    setSlots(s => { const rest = { ...s }; delete rest[id]; return rest; });
  }, []);
  const host = useMemo<WindowStepHost>(() => ({ push, update, pop, slots }), [push, update, pop, slots]);

  // Стабильные ref-колбэки мест: новый колбэк на каждой отрисовке звал бы setSlots без конца.
  const refs = useRef(new Map<string, (el: HTMLElement | null) => void>());
  function slotRef(id: string, kind: keyof WindowStepSlots) {
    const key = `${id}:${kind}`;
    let ref = refs.current.get(key);
    if (!ref) {
      ref = el => setSlots(s => s[id]?.[kind] === el || (!el && !s[id]) ? s : { ...s, [id]: { ...s[id], [kind]: el } });
      refs.current.set(key, ref);
    }
    return ref;
  }

  const top = steps.at(-1) ?? null;
  const topId = top?.id;
  const touchTop = () => {
    if (topId && !top?.touched) setSteps(s => s.map(x => x.id === topId ? { ...x, touched: true } : x));
  };
  useEffect(() => {
    // Ушли с шага — к кнопке, которая его открыла: она снова видна, будь она в форме окна или в шаге
    // под снятым.
    const back = restore.current;
    restore.current = null;
    if (back?.isConnected && !back.closest('[hidden]')) { back.focus(); return; }
    if (!topId) return;
    // Кнопка, открывшая шаг, скрылась вместе с формой, но браузер снимет с неё фокус не сразу —
    // поэтому «вне окна» мало: фокус в скрытом тоже не годится. Окно держит его, пока шаг не взял свой.
    const active = document.activeElement;
    if (!content.current?.contains(active) || active?.closest('[hidden]')) content.current?.focus();
  }, [topId, content]);

  const stepSlot = (kind: keyof WindowStepSlots) => steps.map(s => (
    <div key={s.id} ref={slotRef(s.id, kind)} className="contents" hidden={s.id !== topId} />
  ));
  return { host, top, touchTop, stepSlot };
}
