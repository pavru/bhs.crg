import { createContext, useContext, useEffect } from 'react';

/**
 * Заголовок вкладки браузера = текущее положение в приложении (раздел, а при открытой сущности —
 * её имя). Один писатель `document.title`: `DocumentTitleManager` считает РАЗДЕЛ по маршруту, а
 * экран с открытой сущностью проталкивает ДЕТАЛЬ через `useDocumentTitle(...)` — деталь замещает
 * раздел. Формат: `{деталь ?? раздел} · {название экземпляра}`.
 *
 * Деталь — одно значение (LAST writer wins). Маршруты взаимоисключающи (одновременно смонтирован
 * ровно один detail-экран), поэтому конфликта нет; вложенную сущность (документ поверх комплекта)
 * компонует сам родитель (SetDetail даёт «Документ — Комплект»), а не второй писатель.
 */
export const DetailCtx = createContext<(detail: string | null) => void>(() => {});

/**
 * Заголовок вкладки из положения в приложении и названия экземпляра (ТЗ CORE-25.1, issue #967).
 * Отдельной функцией — чтобы формат проверялся тестом, а не глазами по вкладке.
 */
export function documentTitle(base: string | null, productName: string): string {
  return base ? `${base} · ${productName}` : productName;
}

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
