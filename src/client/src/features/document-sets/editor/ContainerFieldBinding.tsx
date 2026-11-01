import { useMemo, useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { LinkOff } from '@/shared/ui/icons';
import { Modal, ModalAction, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { SourceBindingTrigger } from './SourceBindingTrigger';
import { MappingEditor } from './DataSetsTab';
import {
  useAvailableDataSetFiles, useCreateDataSetBinding, useUpdateDataSetBinding, useDeleteDataSetBinding,
} from '@/shared/api/datasets';
import type { DataSetBinding, DataSetSource, DocumentType, CatalogScope } from '@/shared/api/types';
import { SCOPE_LABELS } from '@/shared/api/types';
import type { SchemaField } from '@/shared/api/schema';
import { isLockedField } from '../fields';
import { Select, SelectItem } from '@/shared/ui/Select';

type FlatSource = DataSetSource & { fileName: string; fileScope: CatalogScope };

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
    const flat = files.flatMap(f => f.sources.map(s => ({ ...s, fileName: f.name, fileScope: f.scope })));
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

  /**
   * Запертое поле (ТЗ CORE-20.2) источником не заполняется. Сегодня контейнер под замком не
   * заводится — объявление модуля принимает только самодостаточные виды значения, — но модалка
   * тут единственный выход для привязки, ДОСТАВШЕЙСЯ от прежней версии типа: снять можно, завести
   * нельзя. Условие здесь, а не только у вызывающего: спрятать кнопку и оставить действие рабочим
   * значит закрыть дверь, не заперев её.
   */
  const locked = isLockedField(field);
  const saveDisabled = locked || !selectedSource || busy || (refMode && !mapping[field.key]) || (isTabular && !isMaterialized && Object.keys(mapping).length === 0);

  return (
    <>
      <SourceBindingTrigger locked={locked} isBound={isBound} onOpen={() => onOpenChange(true)} />

      <Modal open={open} onOpenChange={onOpenChange} title={`Привязка «${field.title}» к источнику`} wide
        footer={<>
          {isBound && (
            <ModalAction danger lead onClick={unbind} disabled={busy} icon={<Icon icon={LinkOff} size={18} />}>
              Отвязать
            </ModalAction>
          )}
          <ModalExit />
          {!locked && (
          <ModalPrimary onClick={save} disabled={saveDisabled} loading={busy}>
            {isBound ? 'Изменить' : 'Привязать'}
          </ModalPrimary>
          )}
        </>}>
        <div className="space-y-4">
          {sources.length === 0 ? (
            <p className="text-body-small text-on-surface-variant py-2">
              Нет подходящих источников для этого поля. {isTabular || isComplexRef
                ? 'Загрузите набор данных на странице «Наборы данных» или в панели уровня.'
                : `Нужен источник, материализованный в тип «${allDocTypes.find(t => t.id === field.typeId)?.name ?? 'этого поля'}» (настраивается на источнике).`}
            </p>
          ) : (
            <>
              <div>
                <Select label="Источник данных" value={sourceId} onValueChange={id => { setSourceId(id); setMapping({}); }}
                  placeholder="— выберите источник —">
                  {sources.map(s => (
                    <SelectItem key={s.id} value={s.id}>
                      [{SCOPE_LABELS[s.fileScope]}] {s.fileName} · {s.name}{s.materializeTypeId ? ` (материализация → ${allDocTypes.find(t => t.id === s.materializeTypeId)?.name ?? 'тип'})` : ''}{s.origin === 'Recognized' ? ' — распознано со скана' : ''}
                    </SelectItem>
                  ))}
                </Select>
              </div>

              {selectedSource && (isMaterialized ? (
                <div className="rounded-md border border-primary/40 bg-primary/5 p-3 text-body-small text-on-surface-variant">
                  Источник материализуется в тип <b>{allDocTypes.find(t => t.id === selectedSource.materializeTypeId)?.name ?? '—'}</b> —
                  {selectedSource.materializeByIdColumn
                    // Режим «по Ид» (issue #725): маппинга нет вовсе, и обещать его здесь значило бы
                    // отправить человека искать несуществующую настройку.
                    ? <> строки ссылаются на существующие документы (колонка «{selectedSource.materializeByIdColumn}»),
                        поле заполнится их живыми данными.</>
                    : <> маппинг задан на источнике, поле заполнится напрямую.</>}
                </div>
              ) : (
                <div className="rounded-md border border-outline-variant p-3">
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
