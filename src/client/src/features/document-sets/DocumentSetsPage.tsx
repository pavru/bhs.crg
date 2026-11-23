import type { ReactNode } from 'react';
import { Routes, Route } from 'react-router';
import { useCan } from '@/shared/api/access';
import { NoAccessPage } from '@/shared/ui/NoAccessPage';
import { RequireAccess } from '@/shared/ui/ProtectedRoute';
import { SetDetail } from './SetDetail';
import { DocumentEditorPage } from './DocumentEditorPage';
import { SectionDetail } from './SectionDetail';
import { ConstructionDetail } from './ConstructionDetail';
import { ConstructionsList } from './ConstructionsList';

// ── Маршруты раздела «Стройки» ───────────────────────────────────────────────
// Экраны вынесены по файлам (#488); здесь остаётся только карта маршрутов.

export function DocumentSetsPage() {
  const set = <IdModuleOnly><SetDetail /></IdModuleOnly>;
  return (
    <Routes>
      <Route element={<RequireAccess />}>
        <Route index element={<ConstructionsList />} />
        {/* Панель — из реестра `constructionPanels.ts` (plan-02). `:panel/*`, а не `:panel`: адрес панели
            не зависит от способа показа, а «смета → редакция → строки» требует вложенных адресов.
            Голый адрес стройки и раздела перенаправляет на панель по умолчанию. */}
        <Route path=":constructionId" element={<ConstructionDetail />} />
        <Route path=":constructionId/:panel/*" element={<ConstructionDetail />} />
        <Route path=":constructionId/sections/:sectionId" element={<SectionDetail />} />
        <Route path=":constructionId/sections/:sectionId/:panel/*" element={<SectionDetail />} />
      </Route>
      <Route path=":constructionId/sets/:setId" element={set} />
      <Route path=":constructionId/sets/:setId/:panel" element={set} />
      {/* Документ — страница, а не окно над комплектом (issue #1384). */}
      <Route path=":constructionId/sets/:setId/docs/:instanceId"
        element={<IdModuleOnly><DocumentEditorPage /></IdModuleOnly>} />
    </Routes>
  );
}

/**
 * Прямой адрес комплекта относится к модулю ИД (issue #1128). Доступ к разделу «Стройки» сам
 * по себе не даёт доступа к комплекту.
 */
function IdModuleOnly({ children }: { children: ReactNode }) {
  const can = useCan();
  if (can.module('id')) return children;
  const title = can.access.modules.find(m => m.code === 'id')?.title ?? 'id';
  return <NoAccessPage what="Комплект документов" missing={{ kind: 'module', code: 'id', title }} />;
}
