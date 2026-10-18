import { createContext, useContext, useEffect, useMemo, useState } from 'react';

/**
 * Что экран сообщает плавающей кнопке основного действия (issue #1434):
 * - `cover` — карточка закрывает список: на телефоне она занимает его место, и «Счёт из файла» поверх
 *   формы счёта закрывал бы её поля и кнопки. Сообщает каркас (`ListDetailShell`), а не экран;
 * - `busy` — кнопка действия на экране занята (идёт загрузка, заводится черновик). На телефоне её
 *   место заняла плавающая, и занятость показывает она (`PrimaryActionButton`).
 *
 * Оба — счётчики, а не флажки: два сообщивших не сбрасывают друг друга, и снятие одного не открывает
 * то, что держит другой (ревью PR #1502).
 */
export interface PrimaryFabReport {
  cover: (on: boolean) => void;
  busy: (id: string, on: boolean) => void;
}

export const PrimaryFabContext = createContext<PrimaryFabReport>({ cover: () => {}, busy: () => {} });

/** Состояние плавающей кнопки у оболочки: что ей сообщили экраны. */
export function usePrimaryFabState() {
  const [covers, setCovers] = useState(0);
  const [busy, setBusy] = useState<ReadonlyMap<string, number>>(new Map());
  const report = useMemo<PrimaryFabReport>(() => ({
    cover: on => setCovers(n => n + (on ? 1 : -1)),
    busy: (id, on) => setBusy(prev => {
      const next = new Map(prev);
      const n = (next.get(id) ?? 0) + (on ? 1 : -1);
      if (n > 0) next.set(id, n);
      else next.delete(id);
      return next;
    }),
  }), []);
  return { covered: covers > 0, isBusy: (id: string) => busy.has(id), report };
}

/** Пока `covered` — плавающей кнопки нет. */
export function useCoversPrimaryFab(covered: boolean): void {
  const { cover } = useContext(PrimaryFabContext);
  useEffect(() => {
    if (!covered) return;
    cover(true);
    return () => cover(false);
  }, [cover, covered]);
}

/** Пока `busy` — плавающая кнопка действия `id` занята. */
export function useReportsPrimaryBusy(id: string, busy: boolean): void {
  const { busy: report } = useContext(PrimaryFabContext);
  useEffect(() => {
    if (!busy) return;
    report(id, true);
    return () => report(id, false);
  }, [report, id, busy]);
}
