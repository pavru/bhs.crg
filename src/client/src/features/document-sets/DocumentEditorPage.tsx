import { useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import { FileText } from 'lucide-react';
import { EmptyState } from '@/shared/ui/EmptyState';
import { ErrorBoundary } from '@/shared/ui/ErrorBoundary';
import { useDocumentTitle } from '@/shared/ui/DocumentTitle';
import { useListDocumentTypes } from '@/shared/api/documentTypes';
import { useGetDocumentSet, useGetAvailableInstances } from '@/shared/api/documentSets';
import type { DocumentInstance } from '@/shared/api/types';
import { InstanceEditor } from './editor';

/**
 * Редактор документа — страница со своим адресом (issue #1384), а не окно во весь экран над
 * комплектом. Окном он держал над собой всё, что открывал сам: пикеры, замечания, проверку, письмо,
 * таблицы полей — это были окна поверх окна. Страницей те же окна стоят над страницей, как везде.
 * Заодно у документа есть адрес, которым делятся, и «Назад» браузера возвращает к комплекту.
 *
 * Уход с несохранённым спрашивает сам редактор (`useLeaveGuard`): сохранение живёт в нём.
 */
export function DocumentEditorPage() {
  const { constructionId, setId, instanceId } = useParams<{ constructionId: string; setId: string; instanceId: string }>();
  const navigate = useNavigate();
  const { data: set, isLoading } = useGetDocumentSet(setId);
  const { data: availableInstances = [] } = useGetAvailableInstances(setId);
  const { data: docTypes = [] } = useListDocumentTypes();
  const setPath = `/document-sets/${constructionId}/sets/${setId}`;

  /**
   * Снимок документа с правками. Пока правок нет, документ — то, что вернул сервер; с правками —
   * закреплённый снимок: другой человек удалил документ, пока его правят, и редактор не обязан
   * исчезнуть мимо вопроса о несохранённом (ревью PR #865, было в окне комплекта).
   */
  const [pinned, setPinned] = useState<DocumentInstance | null>(null);
  const live = set?.instances.find(i => i.id === instanceId) ?? null;
  const instance = live ?? (pinned?.id === instanceId ? pinned : null);
  const docType = instance ? docTypes.find(t => t.id === instance.documentTypeId) : undefined;

  const docName = instance ? (instance.name || docType?.name || 'Документ') : null;
  useDocumentTitle(set ? (docName ? `${docName} — «${set.name}»` : `Комплект «${set.name}»`) : null);

  const back = () => navigate(setPath);

  if (isLoading) return <div className="p-6 text-sm text-fg4">Загрузка...</div>;
  if (!set) return <div className="p-6 text-sm text-danger">Комплект не найден</div>;
  if (!instance) {
    return (
      <EmptyState className="m-6" icon={<FileText size={28} />} title="Документа в комплекте нет"
        description="Его удалили или перенесли в другой комплект."
        action={<Link to={setPath} className="text-sm text-brand-hover">К комплекту «{set.name}»</Link>} />
    );
  }

  return (
    <div className="h-full flex flex-col min-h-0" data-testid="document-editor">
      {/* Граница ошибок (issue #305): краш рендера редактора — панель ошибки с выходом к комплекту,
          а не пустой экран. resetKeys=[id] — другой документ сбрасывает ошибку. */}
      <ErrorBoundary variant="inline" allowReload resetKeys={[instance.id]}
        title="Не удалось открыть документ" onClose={back}>
        <InstanceEditor key={instance.id} instance={instance} setId={set.id} docType={docType}
          allDocTypes={docTypes} otherInstances={availableInstances.filter(i => i.id !== instance.id)}
          onClose={back}
          onDirtyChange={dirty => { if (dirty && live && pinned !== live) setPinned(live); }} />
      </ErrorBoundary>
    </div>
  );
}
