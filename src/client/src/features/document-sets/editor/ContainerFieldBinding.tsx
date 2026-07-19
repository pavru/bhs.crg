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
 * Per-field привязка КОНТЕЙНЕРНОГО поля (array/doc-array/complex/doc-ref) к источнику (issue #296,
 * фаза 2a — «линза»). Модалка (тяжесть аффорданса = тяжести привязки): материализованный источник
 * совместимого типа → типизированный указатель (без маппинга); array/doc-array + обычный источник →
 * табличный маппинг элемента (переиспользуем MappingEditor). Привязка — своя, targetFieldKey=это поле.
 * Ref-маппинг составных/doc-ref по каталогу (scalar-slice) — фаза 2b.
 */
export function ContainerFieldBinding({ instanceId, setId, field, allDocTypes, bindings }: {
  instanceId: string;
  setId: string;
  field: SchemaField;
  allDocTypes: DocumentType[];
  bindings: DataSetBinding[];
}) {
  const [open, setOpen] = useState(false);
  const existing = bindings.find(b => b.targetFieldKey === field.key);
  const isBound = !!existing;

  const { data: files = [] } = useAvailableDataSetFiles(setId);
  const create = useCreateDataSetBinding();
  const update = useUpdateDataSetBinding();
  const del = useDeleteDataSetBinding();

  const isTabular = field.type === 'array' || field.type === 'doc-array';

  // Совместимые источники: материализованные того же типа (указатель) + обычные (только для табличных).
  const sources: FlatSource[] = useMemo(() => {
    const flat = files.flatMap(f => f.sources.map(s => ({ ...s, fileName: f.name })));
    return flat.filter(s => {
      if (s.materializeTypeId) return !!field.typeId && isSameOrDescendant(s.materializeTypeId, field.typeId, allDocTypes);
      return isTabular; // обычный источник подходит только табличному полю
    });
  }, [files, field.typeId, isTabular, allDocTypes]);

  const [sourceId, setSourceId] = useState('');
  const [mapping, setMapping] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);

  const selectedSource = sources.find(s => s.id === sourceId);
  const isMaterialized = !!selectedSource?.materializeTypeId;

  function onOpenChange(o: boolean) {
    setOpen(o);
    if (o) {
      setSourceId(existing?.sourceId ?? '');
      setMapping(existing?.mapping ?? {});
    }
  }

  async function save() {
    if (!selectedSource) return;
    setBusy(true);
    try {
      const map = isMaterialized ? {} : mapping;
      if (existing && existing.sourceId === selectedSource.id) {
        await update.mutateAsync({ id: existing.id, ownerId: instanceId, targetFieldKey: field.key, mapping: map });
      } else {
        if (existing) await del.mutateAsync({ id: existing.id, ownerId: instanceId });
        await create.mutateAsync({ ownerId: instanceId, sourceId: selectedSource.id, targetFieldKey: field.key, mapping: map });
      }
      setOpen(false);
    } finally { setBusy(false); }
  }

  async function unbind() {
    if (!existing) return;
    setBusy(true);
    try { await del.mutateAsync({ id: existing.id, ownerId: instanceId }); setOpen(false); }
    finally { setBusy(false); }
  }

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
              <Button variant="filled" size="sm" onClick={save} disabled={!selectedSource || busy} loading={busy}>
                {isBound ? 'Изменить' : 'Привязать'}
              </Button>
            </div>
          </div>
        }>
        <div className="space-y-4 min-w-[520px]">
          {sources.length === 0 ? (
            <p className="text-xs text-fg4 py-2">
              Нет подходящих источников для этого поля. {isTabular
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
                  <MappingEditor source={selectedSource} schemaFields={[]} tabularFields={[field]}
                    allDocTypes={allDocTypes} mapping={mapping} targetFieldKey={field.key}
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
