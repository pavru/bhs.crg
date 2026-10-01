import type { ReactNode } from 'react';
import { Routes, Route } from 'react-router';
import { useCan } from '@/shared/api/access';
import { NoAccessPage } from '@/shared/ui/NoAccessPage';
import { SetDetail } from './SetDetail';
import { SectionDetail } from './SectionDetail';
import { ConstructionDetail } from './ConstructionDetail';
import { ConstructionsList } from './ConstructionsList';

// ── Маршруты раздела «Стройки» ───────────────────────────────────────────────
// Экраны вынесены по файлам (#488); здесь остаётся только карта маршрутов.

export function DocumentSetsPage() {
  const set = <IdModuleOnly><SetDetail /></IdModuleOnly>;
  return (
    <Routes>
      <Route index element={<ConstructionsList />} />
      <Route path=":constructionId" element={<ConstructionDetail />} />
      <Route path=":constructionId/:panel" element={<ConstructionDetail />} />
      <Route path=":constructionId/sections/:sectionId" element={<SectionDetail />} />
      <Route path=":constructionId/sections/:sectionId/:panel" element={<SectionDetail />} />
      <Route path=":constructionId/sets/:setId" element={set} />
      <Route path=":constructionId/sets/:setId/:panel" element={set} />
    </Routes>
  );
}

/**
 * Комплект — модуль ИД, а раздел «Стройки» под ним — ядро (issue #1128). Ворота раздела
 * (`RequireAccess`) пропускают всякого, кто видит стройки, поэтому прямой адрес комплекта — закладка,
 * ссылка из письма или уведомления — у пользователя без модуля открывал бы экран из одних отказов 403.
 * Здесь — та же страница «недоступно» с названием модуля, что у закрытых разделов.
 *
 * Пока доступ грузится, сюда не доходят: `RequireAccess` раздела ждёт тот же ответ.
 */
function IdModuleOnly({ children }: { children: ReactNode }) {
  const can = useCan();
  if (can.module('id')) return children;
  const title = can.access.modules.find(m => m.code === 'id')?.title ?? 'id';
  return <NoAccessPage what="Комплект документов" missing={{ kind: 'module', code: 'id', title }} />;
}
