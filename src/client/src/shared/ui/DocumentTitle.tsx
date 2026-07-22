import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import { useLocation } from 'react-router-dom';

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
const APP_NAME = 'BHS.CRG';

const SECTION_TITLES: Record<string, string> = {
  'document-sets': 'Комплекты',
  'common-data': 'Общие данные',
  'datasets': 'Наборы данных',
  'quality-docs': 'Документы качества',
  'templates': 'Шаблоны',
  'document-types': 'Типы документов',
  'composite-types': 'Составные типы',
  'field-types': 'Типы полей',
  'users': 'Пользователи',
  'settings': 'Настройки',
  'profile': 'Профиль',
  'login': 'Вход',
};

function sectionFor(pathname: string): string | null {
  const seg = pathname.split('/').filter(Boolean)[0];
  if (!seg) return SECTION_TITLES['document-sets']; // корень → редирект на комплекты
  return SECTION_TITLES[seg] ?? null;
}

const DetailCtx = createContext<(detail: string | null) => void>(() => {});

export function DocumentTitleManager({ children }: { children: ReactNode }) {
  const location = useLocation();
  const [detail, setDetail] = useState<string | null>(null);

  useEffect(() => {
    const base = detail ?? sectionFor(location.pathname);
    document.title = base ? `${base} · ${APP_NAME}` : APP_NAME;
  }, [location.pathname, detail]);

  return <DetailCtx.Provider value={setDetail}>{children}</DetailCtx.Provider>;
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
