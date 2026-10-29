import { useCallback, useState } from 'react';
import { Link, useLocation, useNavigate, useParams } from 'react-router';
import { Icon } from '@/shared/ui/Icon';
import { Description } from '@/shared/ui/icons';
import { EmptyState } from '@/shared/ui/EmptyState';
import { ErrorBoundary } from '@/shared/ui/ErrorBoundary';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { useDocumentTitle } from '@/shared/ui/DocumentTitle';
import { stepBackApproved, useGuardedLeave, useLeaveGuard } from '@/shared/ui/NavigationGuard';
import { useListDocumentTypes } from '@/shared/api/documentTypes';
import { useGetDocumentSet, useGetAvailableInstances } from '@/shared/api/documentSets';
import type { DocumentInstance, DocumentSet, DocumentType } from '@/shared/api/types';
import { FROM_SET } from './documentPath';
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
  const { data: set, isLoading } = useGetDocumentSet(setId);
  // Типы ждём вместе с комплектом (ревью PR #1388): реквизиты нового документа берут значения по
  // умолчанию из схемы ОДИН раз, при первом показе, — пришедшая следом схема их уже не применит.
  const { data: docTypes, isLoading: typesLoading } = useListDocumentTypes();
  const setPath = `/document-sets/${constructionId}/sets/${setId}`;

  if (isLoading || typesLoading) return <div className="p-6 text-body-medium text-on-surface-variant">Загрузка...</div>;
  if (!set) return <div className="p-6 text-body-medium text-error">Комплект не найден</div>;
  return <EditorFrame key={instanceId} set={set} setPath={setPath} instanceId={instanceId!} docTypes={docTypes ?? []} />;
}

/** Состояние одного открытого документа — правки, падение, снимок; другой документ заводит своё. */
function EditorFrame({ set, setPath, instanceId, docTypes }: {
  set: DocumentSet; setPath: string; instanceId: string; docTypes: DocumentType[];
}) {
  const navigate = useNavigate();
  const location = useLocation();
  const { data: availableInstances = [] } = useGetAvailableInstances(set.id);

  /**
   * Снимок документа с правками. Пока правок нет, документ — то, что вернул сервер; с правками —
   * закреплённый снимок: другой человек удалил документ, пока его правят, и редактор не обязан
   * исчезнуть мимо вопроса о несохранённом (ревью PR #865, было в окне комплекта).
   */
  const [pinned, setPinned] = useState<DocumentInstance | null>(null);
  const live = set.instances.find(i => i.id === instanceId) ?? null;
  const instance = live ?? pinned;
  const docType = instance ? docTypes.find(t => t.id === instance.documentTypeId) : undefined;
  const [dirty, setDirty] = useState(false);
  const onDirtyChange = useCallback((d: boolean) => {
    setDirty(d);
    if (d && live) setPinned(live);
  }, [live]);

  const docName = instance ? (instance.name || docType?.name || 'Документ') : null;
  useDocumentTitle(docName ? `${docName} — «${set.name}»` : `Комплект «${set.name}»`);

  /**
   * Выход к комплекту. Пришли из него — шаг истории назад, к тому же месту: новая запись дала бы
   * «комплект → документ → комплект», и «Назад» с комплекта снова открывал бы документ (ревью PR
   * #1388). Пришли по ссылке — замена записи документа комплектом, по той же причине.
   */
  const cameFromSet = (location.state as typeof FROM_SET | null)?.from === FROM_SET.from;
  const back = () => cameFromSet ? stepBackApproved() : navigate(setPath, { replace: true });

  /**
   * Упавший редактор уносит с собой и правки, и их охрану: вопрос о несохранённом задавал он сам.
   * Выход из панели ошибки, меню и «Назад» при набранном тогда спрашивает страница — набранное
   * пропадает, и молча это делать нельзя (ревью PR #1312, PR #1388).
   */
  const [crashed, setCrashed] = useState(false);
  const [leave, setLeave] = useState<(() => void) | null>(null);
  useLeaveGuard(crashed && dirty, proceed => setLeave(() => proceed));
  const guardedLeave = useGuardedLeave();

  if (!instance) {
    return (
      <EmptyState className="m-6" icon={<Icon icon={Description} size={40} />} title="Документа в комплекте нет"
        description="Его удалили или перенесли в другой комплект."
        action={<Link to={setPath} className="text-body-medium text-primary">К комплекту «{set.name}»</Link>} />
    );
  }

  return (
    <div className="h-full flex flex-col min-h-0" data-testid="document-editor">
      {/* Граница ошибок (issue #305): краш рендера редактора — панель ошибки с выходом к комплекту,
          а не пустой экран. «Повторить» заводит редактор заново — с чистыми правками. */}
      <ErrorBoundary variant="inline" allowReload resetKeys={[instance.id]}
        title="Не удалось открыть документ" onClose={() => guardedLeave(back)} onCatch={() => setCrashed(true)}>
        <InstanceEditor instance={instance} setId={set.id} docType={docType}
          allDocTypes={docTypes} otherInstances={availableInstances.filter(i => i.id !== instance.id)}
          onClose={back} onDirtyChange={onDirtyChange} />
      </ErrorBoundary>
      <ConfirmDialog open={!!leave} onOpenChange={o => { if (!o) setLeave(null); }}
        title="Уйти без сохранения?"
        description="Редактор документа упал, и набранное в нём не сохранено — оно пропадёт."
        confirmLabel="Уйти, не сохраняя" cancelLabel="Остаться"
        onConfirm={() => { const go = leave; setLeave(null); go?.(); }} />
    </div>
  );
}
