import { useEffect, useState, type ReactNode } from 'react';
import { useLocation } from 'react-router';
import { DetailCtx } from './DocumentTitle';

/**
 * Единственный писатель `document.title` — считает РАЗДЕЛ по маршруту. Отдельным файлом от
 * `useDocumentTitle` (issue #858): модуль, экспортирующий и компонент, и хук, теряет горячую
 * перезагрузку — правка компонента перезагружает страницу целиком вместо подмены на месте.
 */
const APP_NAME = 'BHS.CRG';

const SECTION_TITLES: Record<string, string> = {
  'document-sets': 'Комплекты',
  'common-data': 'Общие данные',
  'datasets': 'Наборы данных',
  'quality-docs': 'Документы качества',
  'reconciliations': 'Сверка',
  'templates': 'Шаблоны',
  'document-types': 'Типы документов',
  'composite-types': 'Составные типы',
  'field-types': 'Типы полей',
  'users': 'Пользователи',
  'recognition-profiles': 'Профили распознавания',
  'bug-reports': 'Сообщения об ошибках',
  'settings': 'Настройки',
  'profile': 'Профиль',
  'login': 'Вход',
};

function sectionFor(pathname: string): string | null {
  const seg = pathname.split('/').filter(Boolean)[0];
  if (!seg) return SECTION_TITLES['document-sets']; // корень → редирект на комплекты
  return SECTION_TITLES[seg] ?? null;
}

export function DocumentTitleManager({ children }: { children: ReactNode }) {
  const location = useLocation();
  const [detail, setDetail] = useState<string | null>(null);

  useEffect(() => {
    const base = detail ?? sectionFor(location.pathname);
    document.title = base ? `${base} · ${APP_NAME}` : APP_NAME;
  }, [location.pathname, detail]);

  return <DetailCtx.Provider value={setDetail}>{children}</DetailCtx.Provider>;
}
