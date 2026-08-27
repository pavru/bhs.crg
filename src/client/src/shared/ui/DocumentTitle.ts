import { createContext, useContext, useEffect } from 'react';

/**
 * Заголовок вкладки браузера = текущее положение в приложении (раздел, а при открытой сущности —
 * её имя). Один писатель `document.title`: `DocumentTitleManager` считает РАЗДЕЛ по маршруту, а
 * экран с открытой сущностью проталкивает ДЕТАЛЬ через `useDocumentTitle(...)` — деталь замещает
 * раздел. Формат: `{деталь ?? раздел} · BHS.CRG`.
 *
 * Деталь — одно значение (LAST writer wins). Маршруты взаимоисключающи (одновременно смонтирован
 * ровно один detail-экран), поэтому конфликта нет; вложенную сущность (документ поверх комплекта)
 * компонует сам родитель (SetDetail даёт «Документ — Комплект»), а не второй писатель.
 */
export const DetailCtx = createContext<(detail: string | null) => void>(() => {});

/**
 * Экран с открытой сущностью задаёт деталь заголовка (имя сущности). `null`/`undefined` — детали нет
 * (показываем раздел). Деталь снимается при размонтировании экрана. Вызывать безусловно (до ранних
 * return-ов), передавая null пока данные грузятся.
 */
export function useDocumentTitle(detail: string | null | undefined): void {
  const setDetail = useContext(DetailCtx);
  useEffect(() => {
    setDetail(detail ?? null);
    return () => setDetail(null);
  }, [setDetail, detail]);
}
