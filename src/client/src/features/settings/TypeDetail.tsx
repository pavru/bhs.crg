/**
 * Правая часть list-detail: выбранный тип со всеми его редакторами — часть страницы типов.
 *
 * Выделено из `DocumentTypesPage.tsx` (issue #1031). `findReferencingTypes` оставлен приватным:
 * он нужен только предупреждению об удалении.
 */
import { useState } from 'react';
import { Trash2, Copy, Database, ShieldCheck } from 'lucide-react';
import { BindingTemplatesDialog } from './BindingTemplatesDialog';
import { TypeAuditModal } from './TypeAuditModal';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { useDeleteDocumentType, useDocumentTypeUsage, useSetDocumentTypeGroup } from '@/shared/api/documentTypes';
import { GroupPicker } from './TypeGroupAccordion';
import type { DocumentType } from '@/shared/api/types';
import { chainFieldKeys, parseSchemaFields, resolveEffectiveFields, type SchemaField, type SchemaDefinition } from '@/shared/api/schema';
import { typeHealth, healthBadgeLabel } from './typeHealth';
import { TYPE_LABELS } from './schemaConstants';
import { DetailHeader } from '@/shared/ui/ListDetailShell';
import { RowActionsMenu } from '@/shared/ui/RowActionsMenu';
import { useToast } from '@/shared/ui/Toast';
import { PropertiesEditor } from './PropertiesEditor';
import { SchemaEditor } from './SchemaEditor';

/** Типы, ссылающиеся на данный тип полем complex/array/doc-ref/doc-array (по собственной схеме). */
function findReferencingTypes(id: string, allDocTypes: DocumentType[]): DocumentType[] {
  return allDocTypes.filter(dt => dt.id !== id
    && parseSchemaFields(dt.schema).some(f =>
      (f.type === 'complex' || f.type === 'array' || f.type === 'doc-ref' || f.type === 'doc-array') && f.typeId === id));
}

/** Правая панель list-detail (issue #197 Фаза A): шапка типа (метрики+действия) + редактор как есть. */
export function TypeDetail({ docType, allDocTypes, allGroups, onDeleted, dirty, saving, onSaveAll, onRevert, onDuplicate, onSelectType }: {
  docType: DocumentType; allDocTypes: DocumentType[]; allGroups: string[]; onDeleted: () => void;
  dirty: boolean; saving: boolean; onSaveAll: () => Promise<void>; onRevert: () => void; onDuplicate: () => void;
  onSelectType: (id: string) => void;
}) {
  const deleteMutation = useDeleteDocumentType();
  const { data: usage } = useDocumentTypeUsage(docType.id);
  const toast = useToast();
  const groupMutation = useSetDocumentTypeGroup();
  const [templatesOpen, setTemplatesOpen] = useState(false);
  const [deleteConfirmOpen, setDeleteConfirmOpen] = useState(false);
  const [auditOpen, setAuditOpen] = useState(false);

  const effectiveFields = resolveEffectiveFields(docType, allDocTypes);
  const ownFieldCount = parseSchemaFields(docType.schema).length;
  const parentType = docType.parentId ? allDocTypes.find(dt => dt.id === docType.parentId) : null;
  // Типы, ссылающиеся на этот тип полем complex/array/doc-ref/doc-array (для бейджа «используется: N»).
  const referencedBy = findReferencingTypes(docType.id, allDocTypes);
  // Проактивное использование (issue #275): полный набор причин с backend (объекты/шаблоны/качество/
  // привязки/материализация/наследники/подтип). При наличии — диалог удаления открывается сразу в
  // состоянии «нельзя» со списком причин; реактивный 409 остаётся страховкой от гонок.
  const usageReasons = usage?.reasons ?? [];
  const deleteBlockedNode = usageReasons.length > 0 ? (
    <div>
      <p className="mb-1.5 font-medium">Тип используется — сначала снимите зависимости:</p>
      <ul className="list-disc pl-4 space-y-0.5">
        {usageReasons.map(r => (
          <li key={r.kind}>
            {r.label}{r.names.length > 0 ? `: ${r.names.join(', ')}` : r.count > 0 ? `: ${r.count}` : ''}
          </li>
        ))}
      </ul>
    </div>
  ) : undefined;
  const compositeTypes = allDocTypes.filter(dt => dt.kind === 'Composite');
  const requiredCount = effectiveFields.filter(f => f.required).length;
  const complexFields = effectiveFields.filter(f => f.type === 'complex');

  function getFieldTypeLabel(f: SchemaField) {
    if (f.type === 'complex') {
      const ct = compositeTypes.find(c => c.id === f.typeId);
      return ct ? `[${ct.name}]` : '[Составной]';
    }
    return TYPE_LABELS[f.type] ?? f.type;
  }

  const badge = 'text-xs px-2 py-0.5 rounded-full font-medium';
  // Переход по иерархии в обе стороны (issue #784): бейдж родителя и чипы прямых наследников —
  // кнопки, а не подписи. Дойти до родителя иначе можно было только поиском в списке слева, хотя
  // на него ссылается вся работа с наследованием («Как у родителя», «Ключ уже есть в родительском
  // типе»). Родитель и наследники всегда того же kind, так что переход не покидает страницу.
  const jumpBadge = `${badge} truncate max-w-[200px] transition-colors focus-visible:outline-none `
    + 'focus-visible:ring-2 focus-visible:ring-brand focus-visible:ring-offset-2 focus-visible:ring-offset-surface';
  // kind сверяем сами: клиентские пикеры родителя его держат, а команды создания/обновления
  // валидируют только циклы и уникальность — из восстановления бэкапа кросс-kind связь пройдёт.
  const children = allDocTypes
    .filter(t => t.parentId === docType.id && t.kind === docType.kind)
    .sort((a, b) => a.name.localeCompare(b.name, 'ru'));

  // Состояние типа (issue #794): красный чип — то, из-за чего схема не сохранится, жёлтый —
  // «нездоров, но сохраняется». Считаем по сохранённой схеме: правку показывает сама форма, а
  // сюда тип может приехать не своей правкой, а изменением предка.
  const schemaDef = docType.schema as unknown as SchemaDefinition;
  const health = typeHealth(
    parseSchemaFields(docType.schema),
    parentType ? resolveEffectiveFields(parentType, allDocTypes).map(f => f.key) : [],
    parentType ? chainFieldKeys(parentType, allDocTypes) : [],
    {
      groups: schemaDef.groups,
      excludedFields: schemaDef.excludedFields,
      ungroupedOrder: schemaDef.ungroupedOrder,
      fieldOverrides: schemaDef.fieldOverrides,
    },
    schemaDef.typstRenders ?? [],
  );
  const healthLabel = healthBadgeLabel(health);
  return (
    <div className="flex flex-col min-h-0 flex-1">
      {/* Шапка типа — доменные heading/actions поверх общего DetailHeader (issue #210 Этап 1b) */}
      <DetailHeader dirty={dirty} saving={saving} onSaveAll={onSaveAll} onRevert={onRevert}
        heading={
          <>
            <div className="flex items-center gap-2 flex-wrap">
              <h2 className="text-xl font-normal text-fg1 truncate">{docType.name}</h2>
              <span className="text-xs text-fg4 font-mono">{docType.code}</span>
              {parentType && (
                <button type="button" onClick={() => onSelectType(parentType.id)}
                  title="Перейти к родительскому типу"
                  className={`${jumpBadge} bg-brand-subtle text-brand hover:text-brand-hover hover:underline`}>
                  ↑ {parentType.name}
                </button>
              )}
              {docType.isAbstract && <span className={`${badge} bg-warning-subtle text-warning`}>абстрактный</span>}
              {docType.allowsProxy && <span className={`${badge} bg-brand-subtle text-brand`}>роль/прокси</span>}
            </div>
            <div className="flex items-center gap-2 mt-2 flex-wrap">
              {effectiveFields.length > 0 && (
                <span className={`${badge} bg-muted text-fg3`}>
                  {effectiveFields.length} полей{parentType && ownFieldCount > 0 ? ` · ${ownFieldCount} своих` : ''}
                </span>
              )}
              {requiredCount > 0 && <span className={`${badge} bg-muted text-fg3`}>{requiredCount} обязательных</span>}
              {complexFields.length > 0 && (
                <span className={`${badge} bg-muted text-fg3`} title={complexFields.map(getFieldTypeLabel).join(', ')}>
                  {complexFields.length} составных
                </span>
              )}
              {referencedBy.length > 0 && (
                <span className={`${badge} bg-brand-subtle text-brand`} title={`Используется в: ${referencedBy.map(t => t.name).join(', ')}`}>
                  используется: {referencedBy.length}
                </span>
              )}
              {healthLabel.blocking && (
                <span className={`${badge} bg-danger-subtle text-danger`}
                  title={health.blocking.map(b => `• ${b.text}`).join(String.fromCharCode(10))}>
                  ⚠ {healthLabel.blocking}
                </span>
              )}
              {healthLabel.soft && (
                <span className={`${badge} bg-warning-subtle text-warning`}
                  title={health.soft.map(i => `• ${i.text}`).join(String.fromCharCode(10))}>
                  {healthLabel.soft}
                </span>
              )}
            </div>
            {children.length > 0 && (
              <div className="flex items-center gap-1.5 mt-2 flex-wrap">
                <span className="text-xs text-fg4 shrink-0">наследники:</span>
                {children.map(c => (
                  <button key={c.id} type="button" onClick={() => onSelectType(c.id)}
                    title={`Перейти к типу «${c.name}»`}
                    className={`${jumpBadge} bg-muted text-fg2 hover:text-brand-hover hover:underline`}>
                    ↓ {c.name}
                  </button>
                ))}
              </div>
            )}
          </>
        }
        actions={
          <>
            <GroupPicker groups={allGroups} value={docType.group}
              onChange={group => groupMutation.mutate({ id: docType.id, group },
                { onError: e => toast.apiError(e, 'Не удалось изменить группу типа') })} />
            <RowActionsMenu ariaLabel="Действия типа" actions={[
              { key: 'dup', label: 'Дублировать', icon: <Copy size={14} />, onSelect: onDuplicate },
              { key: 'audit', label: 'Аудит инстансов', icon: <ShieldCheck size={14} />, onSelect: () => setAuditOpen(true) },
              ...(docType.kind === 'Document'
                ? [{ key: 'tpl', label: 'Шаблоны данных', icon: <Database size={14} />, onSelect: () => setTemplatesOpen(true) }]
                : []),
              { key: 'del', label: 'Удалить тип', danger: true, disabled: deleteMutation.isPending,
                icon: <Trash2 size={14} />, onSelect: () => setDeleteConfirmOpen(true) },
            ]} />
          </>
        } />
      {/* Тело редактора (существующие редакторы как есть — Фаза A) */}
      <div className="flex-1 min-h-0 overflow-y-auto px-6 py-5">
        <div className="mx-auto max-w-4xl">
          <PropertiesEditor docType={docType} allDocTypes={allDocTypes} />
          <SchemaEditor docType={docType} allDocTypes={allDocTypes} onSelectType={onSelectType} />
        </div>
      </div>
      {templatesOpen && (
        <BindingTemplatesDialog docType={docType} allDocTypes={allDocTypes} onClose={() => setTemplatesOpen(false)} />
      )}
      <TypeAuditModal typeId={docType.id} typeName={docType.name}
        schemaFieldKeys={effectiveFields.map(f => f.key)}
        open={auditOpen} onClose={() => setAuditOpen(false)} />
      <ConfirmDialog
        open={deleteConfirmOpen}
        onOpenChange={setDeleteConfirmOpen}
        title={`Удалить тип «${docType.name}»?`}
        description={<p>Это повлияет на все документы и шаблоны, использующие этот тип. Действие необратимо.</p>}
        confirmLabel={`Удалить тип «${docType.name}»`}
        requireCheckbox="Понимаю, что это необратимо"
        blocked={deleteBlockedNode}
        onConfirm={() => deleteMutation.mutateAsync(docType.id).then(onDeleted)}
      />
    </div>
  );
}
