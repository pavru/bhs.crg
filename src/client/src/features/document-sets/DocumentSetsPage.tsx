import { Routes, Route } from 'react-router';
import { SetDetail } from './SetDetail';
import { SectionDetail } from './SectionDetail';
import { ConstructionDetail } from './ConstructionDetail';
import { ConstructionsList } from './ConstructionsList';

// ── Маршруты раздела «Стройки» ───────────────────────────────────────────────
// Экраны вынесены по файлам (#488); здесь остаётся только карта маршрутов.

export function DocumentSetsPage() {
  return (
    <Routes>
      <Route index element={<ConstructionsList />} />
      <Route path=":constructionId" element={<ConstructionDetail />} />
      <Route path=":constructionId/:panel" element={<ConstructionDetail />} />
      <Route path=":constructionId/sections/:sectionId" element={<SectionDetail />} />
      <Route path=":constructionId/sections/:sectionId/:panel" element={<SectionDetail />} />
      <Route path=":constructionId/sets/:setId" element={<SetDetail />} />
      <Route path=":constructionId/sets/:setId/:panel" element={<SetDetail />} />
    </Routes>
  );
}
