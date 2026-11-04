import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Add, Database, Delete, Description } from '@/shared/ui/icons';
import type { CatalogScope, DocumentInstance, DocumentType, FieldRef } from '@/shared/api/types';
import { isFieldRef, isInstanceRef } from '@/shared/api/types';
import type { SchemaField } from '@/shared/api/schema';
import { applyOrder, dropOrder, appendOrigins, identityOrigins, type PathOrigins } from './arrayRows';
import { InstancePickerModal } from './InstancePickerModal';
import { BROKEN_LABEL, BrokenRefChip, BrokenRefNote } from './BrokenRef';
import { IconButton } from '@/shared/ui/Button';
import { DocumentStatusText } from './DocumentStatusText';
import { Chip } from '@/shared/ui/Chip';
import { StatusText } from '@/shared/ui/StatusText';

export function DocRefField({ field, allDocTypes, value, onChange, otherInstances, setId, broken = false }: {
  field: SchemaField; allDocTypes: DocumentType[]; value: unknown;
  onChange: (val: unknown) => void; otherInstances: DocumentInstance[];
  setId?: string;
  /** Цель ссылки удалена (issue #332): для catalog — из backend-диагностики; instance-промах ловим сами. */
  broken?: boolean;
}) {
  const [pickerOpen, setPickerOpen] = useState(false);
  const iRef = isInstanceRef(value) ? value : null;
  const cRef = isFieldRef(value) && (value as FieldRef).$ref === 'catalog' ? value as FieldRef : null;

  /**
   * Пикер один на все состояния поля (issue #749).
   *
   * <p>Он же выбирает ссылку впервые, он же её заменяет: показывает и записи общих данных, и
   * документы комплекта, а значит из ЛЮБОГО состояния можно перейти в любое другое.</p>
   */
  const picker = (
    <InstancePickerModal open={pickerOpen} onOpenChange={setPickerOpen}
      field={field} allDocTypes={allDocTypes} otherInstances={otherInstances}
      setId={setId} onSelect={ref => onChange(ref)} />
  );

  /**
   * «Заменить ссылку» — рядом со «Снять» в каждом состоянии, где ссылка уже стоит.
   *
   * <p>Была она ровно в одном состоянии из четырёх — у битой instance-ссылки. В остальных трёх
   * (здоровая instance, здоровая и битая catalog) замена делалась в два шага: снять, потом открыть
   * пикер заново — при том что снятие уже стёрло, на что смотреть при выборе замены. Значение — input
   * chip, как у составного поля (<code>RefTile</code>, Р4): щелчок по чипу — заменить, крестик — снять.</p>
   */
  const replace = () => setPickerOpen(true);
  const clear = () => onChange(null);

  if (iRef) {
    const inst = otherInstances.find(i => i.id === iRef.instanceId);
    const dt = inst ? allDocTypes.find(t => t.id === inst.documentTypeId) : undefined;
    const label = inst?.name || dt?.name || iRef.displayName;
    // Instance-промах ловим локально и бесплатно (данные комплекта уже в руках) — без похода в backend.
    const isBroken = broken || !inst;
    if (isBroken) return <BrokenRefChip name={iRef.displayName} onReplace={replace} onClear={clear}>{picker}</BrokenRefChip>;
    return (
      <div className="flex flex-wrap items-center gap-2">
        <Chip icon={Description} title="Заменить ссылку" onClick={replace} onRemove={clear} removeLabel="Снять ссылку"
          className="max-w-full">{label}</Chip>
        {inst?.name && dt && <StatusText>{dt.name}</StatusText>}
        {inst && <DocumentStatusText status={inst.status} />}
        {picker}
      </div>
    );
  }

  if (cRef) {
    if (broken) return <BrokenRefChip name={cRef.displayName} onReplace={replace} onClear={clear}>{picker}</BrokenRefChip>;
    // Вид ссылки — знак общих данных, а не заливка warning-container: тревоги в ней не было.
    return (
      <div className="flex flex-wrap items-center gap-2">
        <Chip icon={Database} title="Заменить ссылку" onClick={replace} onRemove={clear} removeLabel="Снять ссылку"
          className="max-w-full">{cRef.displayName}</Chip>
        <StatusText>Общие данные</StatusText>
        {picker}
      </div>
    );
  }

  return (
    <>
      <button type="button" onClick={() => setPickerOpen(true)}
        className="flex items-center gap-2 px-3 py-2 text-body-medium text-primary border border-dashed border-primary/40 rounded-md hover:bg-primary-container transition-colors w-full">
        <Icon icon={Description} size={18} /> Выбрать документ...
      </button>
      {picker}
    </>
  );
}

export function DocArrayField({ field, allDocTypes, value, onChange, otherInstances, setId, brokenPaths, basePath, savedAt }: {
  field: SchemaField; allDocTypes: DocumentType[]; value: unknown;
  onChange: (val: unknown[]) => void; otherInstances: DocumentInstance[];
  setId?: string;
  /** Пути битых ссылок (issue #332) + базовый путь этого массива — для пометки конкретных элементов. */
  brokenPaths?: Set<string>; basePath?: string;
  /** Отметка времени сохранённого документа: её смена значит «пути диагностики перенумерованы» (#759). */
  savedAt?: string;
}) {
  const [pickerOpen, setPickerOpen] = useState(false);
  const items = (Array.isArray(value) ? value : []).filter(isFieldRef);
  const linkedInstanceIds = new Set(items.map(r => r.instanceId).filter(Boolean));

  // Метки битых ссылок адресуют строку номером и считаются по СОХРАНЁННОМУ состоянию (issue #759):
  // тот же учёт происхождения, что в редакторе массива, и тот же признак сброса — факт сохранения,
  // а не приход диагностики. Порядок здесь не меняют, но удаление сдвигает номера.
  const [marks, setMarks] = useState<{ savedAt?: string; origins: PathOrigins }>(
    () => ({ savedAt, origins: identityOrigins(items.length) }));
  if (marks.savedAt !== savedAt) {
    setMarks({ savedAt, origins: identityOrigins(items.length) });
  }
  const markPath = (i: number) => {
    const origin = marks.origins[i];
    return basePath && origin != null ? `${basePath}[${origin}]` : null;
  };

  function addRef(ref: FieldRef) {
    if (ref.instanceId && linkedInstanceIds.has(ref.instanceId)) return;
    onChange([...items, ref]);
    setMarks(m => ({ ...m, origins: appendOrigins(m.origins, 1) }));
  }

  function removeRef(idx: number) {
    const order = dropOrder(items.length, new Set([idx]));
    onChange(applyOrder(items, order));
    setMarks(m => ({ ...m, origins: applyOrder(m.origins, order) }));
  }

  return (
    <div className="border border-outline-variant rounded-md overflow-hidden">
      <div className="flex items-center justify-between px-3 py-2 bg-surface-container-low border-b border-outline-variant">
        <span className="text-body-small font-medium text-on-surface-variant">
          Документы <span className="text-on-surface-variant">{items.length} ссылок</span>
        </span>
        <button type="button" onClick={() => setPickerOpen(true)}
          className="flex items-center gap-1 text-body-small text-primary px-2 py-0.5 rounded-xs hover:bg-primary-container transition-colors">
          <Icon icon={Add} size={18} /> Добавить
        </button>
      </div>
      {items.length === 0 ? (
        <p className="text-body-small text-on-surface-variant text-center py-3">Нет ссылок — нажмите «Добавить»</p>
      ) : (
        <div className="divide-y divide-surface-container">
          {items.map((ref, i) => {
            const isCatalog = ref.$ref === 'catalog';
            const inst = !isCatalog ? otherInstances.find(inst => inst.id === ref.instanceId) : undefined;
            const dt = inst ? allDocTypes.find(t => t.id === inst.documentTypeId) : undefined;
            const label = inst?.name || dt?.name || ref.displayName;
            // Битый элемент: instance-промах ловим локально; catalog — по backend-пути `basePath[i]`.
            const path = markPath(i);
            const itemBroken = (!isCatalog && !inst)
              || (!!path && !!brokenPaths?.has(path));
            if (itemBroken) {
              return (
                <div key={i}>
                  <div className="flex items-center gap-2 px-3 py-2">
                    <Icon icon={Description} size={18} className="text-error shrink-0" />
                    <span className={`flex-1 min-w-0 text-body-medium truncate ${BROKEN_LABEL}`}>{ref.displayName}</span>
                    <IconButton label={`Удалить ссылку ${i + 1}`} danger onClick={() => removeRef(i)} icon={Delete} iconClassName="text-error" />
                  </div>
                  <BrokenRefNote compact />
                </div>
              );
            }
            return (
              <div key={i} className="flex items-center gap-2 px-3 py-2">
                <Icon icon={Description} size={18} className={isCatalog ? 'text-warning shrink-0' : 'text-primary shrink-0'} />
                <span className="flex-1 min-w-0">
                  <span className="block text-body-medium text-on-surface-variant truncate">{label}</span>
                  {isCatalog ? (
                    <span className="block text-body-small text-warning truncate">Общие данные</span>
                  ) : inst?.name && dt ? (
                    <span className="block text-body-small text-on-surface-variant truncate">{dt.name}</span>
                  ) : null}
                </span>
                {inst && (
                  <DocumentStatusText status={inst.status} />
                )}
                <IconButton label={`Удалить ссылку ${i + 1}`} danger onClick={() => removeRef(i)} icon={Delete} />
              </div>
            );
          })}
        </div>
      )}
      <InstancePickerModal open={pickerOpen} onOpenChange={setPickerOpen}
        field={field} allDocTypes={allDocTypes}
        otherInstances={otherInstances.filter(i => !linkedInstanceIds.has(i.id))}
        setId={setId} onSelect={addRef} />
    </div>
  );
}

// Re-export CatalogScope so DocRefField consumers don't need to import it separately
export type { CatalogScope };
