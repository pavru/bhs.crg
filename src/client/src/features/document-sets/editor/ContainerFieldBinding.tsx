import { useMemo, useState } from 'react';
import { Database, Link2Off } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { MappingEditor } from './DataSetsTab';
import {
  useAvailableDataSetFiles, useCreateDataSetBinding, useUpdateDataSetBinding, useDeleteDataSetBinding,
} from '@/shared/api/datasets';
import type { DataSetBinding, DataSetSource, DocumentType } from '@/shared/api/types';
import type { SchemaField } from '@/shared/api/schema';

type FlatSource = DataSetSource & { fileName: string };

/** Совместимость по наследованию: childId == ancestorId либо потомок ancestorId по parentId. */
function isSameOrDescendant(childId: string, ancestorId: string, allDocTypes: DocumentType[]): boolean {
  let cur: string | null = childId;
  let guard = 0;
  while (cur && guard++ < 32) {
    if (cur === ancestorId) return true;
    cur = allDocTypes.find(t => t.id === cur)?.parentId ?? null;
  }
  return false;
}

/**
 * Per-field привязка КОНТЕЙНЕРНОГО поля к источнику (issue #296, фазы 2a/2b — «линза»). Модалка;
 * три формы под капотом:
 *  - материализованный источник совместимого типа → типизированный указатель (targetFieldKey=поле, {});
 *  - array/doc-array + обычный источник → табличный маппинг элемента (targetFieldKey=поле);
 *  - complex + обычный источник → ref-маппинг каталога по имени/идентификатору — срез общего
 *    (owner,source)-скалярного binding (targetFieldKey=null, mapping[поле]=@@ref…), find-or-create/prune.
 */
export function ContainerFieldBinding({ instanceId, setId, field, allDocTypes, bindings }: {
  instanceId: string;
  setId: string;
  field: SchemaField;
  allDocTypes: DocumentType[];
  bindings: DataSetBinding[];
}) {
  const [open, setOpen] = useState(false);
  const isTabular = field.type === 'array' || field.type === 'doc-array';
  const isComplexRef = field.type === 'complex'; // caталог-ref по имени/идентификатору (scalar-slice)

  // Текущая привязка поля: своя (targetFieldKey) ИЛИ срез скалярного binding (для complex-ref).
  const targetBinding = bindings.find(b => b.targetFieldKey === field.key);
  const sliceBinding = bindings.find(b => !b.targetFieldKey && b.mapping?.[field.key]);
  const isBound = !!targetBinding || !!sliceBinding;

  const { data: files = [] } = useAvailableDataSetFiles(setId);
  const create = useCreateDataSetBinding();
  const update = useUpdateDataSetBinding();
  const del = useDeleteDataSetBinding();

  const sources: FlatSource[] = useMemo(() => {
    const flat = files.flatMap(f => f.sources.map(s => ({ ...s, fileName: f.name })));
    return flat.filter(s => {
      if (s.materializeTypeId) return !!field.typeId && isSameOrDescendant(s.materializeTypeId, field.typeId, allDocTypes);
      return isTabular || isComplexRef; // обычный источник — табличному или составному (ref) полю
    });
  }, [files, field.typeId, isTabular, isComplexRef, allDocTypes]);

  const [sourceId, setSourceId] = useState('');
  const [mapping, setMapping] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);

  const selectedSource = sources.find(s => s.id === sourceId);
  const isMaterialized = !!selectedSource?.materializeTypeId;
  // Режим для несматериализованного источника: табличный (element map) или составной ref-срез.
  const refMode = !!selectedSource && !isMaterialized && isComplexRef;

  function onOpenChange(o: boolean) {
    setOpen(o);
    if (o) {
      if (targetBinding) { setSourceId(targetBinding.sourceId); setMapping(targetBinding.mapping ?? {}); }
      else if (sliceBinding) { setSourceId(sliceBinding.sourceId); setMapping({ [field.key]: sliceBinding.mapping[field.key] }); }
      else { setSourceId(''); setMapping({}); }
    }
  }

  // Убрать поле из среза скалярного binding (prune при опустевшем).
  async function pruneSlice(binding: DataSetBinding) {
    const map = { ...binding.mapping }; delete map[field.key];
    if (Object.keys(map).length === 0) await del.mutateAsync({ id: binding.id, ownerId: instanceId });
    else await update.mutateAsync({ id: binding.id, ownerId: instanceId, targetFieldKey: null, mapping: map });
  }

  async function save() {
    if (!selectedSource) return;
    setBusy(true);
    try {
      if (refMode) {
        // Составной ref → срез (owner, source)-скалярного binding.
        const refExpr = mapping[field.key];
        if (!refExpr) return; // ничего не выбрано
        const scalar = bindings.find(b => !b.targetFieldKey && b.sourceId === selectedSource.id);
        const map = { ...(scalar?.mapping ?? {}) }; map[field.key] = refExpr;
        if (scalar) await update.mutateAsync({ id: scalar.id, ownerId: instanceId, targetFieldKey: null, mapping: map });
        else await create.mutateAsync({ ownerId: instanceId, sourceId: selectedSource.id, targetFieldKey: null, mapping: map });
        if (targetBinding) await del.mutateAsync({ id: targetBinding.id, ownerId: instanceId });
        if (sliceBinding && sliceBinding.sourceId !== selectedSource.id) await pruneSlice(sliceBinding);
      } else {
        // Материализованный указатель или табличный маппинг → своя привязка (targetFieldKey=поле).
        const map = isMaterialized ? {} : mapping;
        if (targetBinding && targetBinding.sourceId === selectedSource.id) {
          await update.mutateAsync({ id: targetBinding.id, ownerId: instanceId, targetFieldKey: field.key, mapping: map });
        } else {
          if (targetBinding) await del.mutateAsync({ id: targetBinding.id, ownerId: instanceId });
          await create.mutateAsync({ ownerId: instanceId, sourceId: selectedSource.id, targetFieldKey: field.key, mapping: map });
        }
        if (sliceBinding) await pruneSlice(sliceBinding);
      }
      setOpen(false);
    } finally { setBusy(false); }
  }

  async function unbind() {
    setBusy(true);
    try {
      if (targetBinding) await del.mutateAsync({ id: targetBinding.id, ownerId: instanceId });
      if (sliceBinding) await pruneSlice(sliceBinding);
      setOpen(false);
    } finally { setBusy(false); }
  }

  const saveDisabled = !selectedSource || busy || (refMode && !mapping[field.key]) || (isTabular && !isMaterialized && Object.keys(mapping).length === 0);

  return (
    <>
      <button type="button" onClick={() => onOpenChange(true)}
        title={isBound ? 'Заполняется из источника — изменить/отвязать' : 'Привязать к источнику данных'}
        aria-label={isBound ? 'Привязка к источнику' : 'Привязать к источнику'}
        className={`inline-flex items-center justify-center rounded transition-colors ${
          isBound ? 'text-brand hover:text-brand-hover' : 'text-fg4 opacity-0 group-hover:opacity-100 hover:text-fg2'}`}>
        <Database size={13} />
      </button>

      <Modal open={open} onOpenChange={onOpenChange} title={`Привязка «${field.title}» к источнику`} wide
        footer={
          <div className="flex items-center justify-between gap-2">
            <div>
              {isBound && (
                <Button variant="text" size="sm" danger onClick={unbind} disabled={busy} icon={<Link2Off size={14} />}>
                  Отвязать
                </Button>
              )}
            </div>
            <div className="flex gap-2">
              <Button variant="text" size="sm" onClick={() => setOpen(false)}>Отмена</Button>
              <Button variant="filled" size="sm" onClick={save} disabled={saveDisabled} loading={busy}>
                {isBound ? 'Изменить' : 'Привязать'}
              </Button>
            </div>
          </div>
        }>
        <div className="space-y-4 min-w-[520px]">
          {sources.length === 0 ? (
            <p className="text-xs text-fg4 py-2">
              Нет подходящих источников для этого поля. {isTabular || isComplexRef
                ? 'Загрузите набор данных на странице «Наборы данных» или в панели уровня.'
                : `Нужен источник, материализованный в тип «${allDocTypes.find(t => t.id === field.typeId)?.name ?? 'этого поля'}» (настраивается на источнике).`}
            </p>
          ) : (
            <>
              <div>
                <label className="block text-xs font-medium text-fg3 mb-1">Источник данных</label>
                <select value={sourceId} onChange={e => { setSourceId(e.target.value); setMapping({}); }}
                  className="w-full border border-stroke rounded-md px-2 py-1.5 text-sm bg-surface text-fg1">
                  <option value="">— выберите источник —</option>
                  {sources.map(s => (
                    <option key={s.id} value={s.id}>
                      {s.fileName} · {s.name}{s.materializeTypeId ? ` (материализация → ${allDocTypes.find(t => t.id === s.materializeTypeId)?.name ?? 'тип'})` : ''}
                    </option>
                  ))}
                </select>
              </div>

              {selectedSource && (isMaterialized ? (
                <div className="rounded-lg border border-brand/40 bg-brand/5 p-3 text-xs text-fg3">
                  Источник материализуется в тип <b>{allDocTypes.find(t => t.id === selectedSource.materializeTypeId)?.name ?? '—'}</b> —
                  маппинг задан на источнике, поле заполнится напрямую.
                </div>
              ) : (
                <div className="rounded-lg border border-stroke p-3">
                  {/* refMode (complex): schemaFields=[field] → ref-маппинг каталога; иначе табличный элемент. */}
                  <MappingEditor source={selectedSource}
                    schemaFields={refMode ? [field] : []}
                    tabularFields={refMode ? [] : [field]}
                    allDocTypes={allDocTypes} mapping={mapping}
                    targetFieldKey={refMode ? null : field.key}
                    onChange={m => setMapping(m)} hideModeSelector />
                </div>
              ))}
            </>
          )}
        </div>
      </Modal>
    </>
  );
}
