import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Add, Delete, Description, LinkOff, Refresh } from '@/shared/ui/icons';
import type { CatalogScope, DocumentInstance, DocumentType, FieldRef } from '@/shared/api/types';
import { isFieldRef, isInstanceRef } from '@/shared/api/types';
import type { SchemaField } from '@/shared/api/schema';
import { STATUS_COLORS, STATUS_LABELS } from './constants';
import { applyOrder, dropOrder, appendOrigins, identityOrigins, type PathOrigins } from './arrayRows';
import { InstancePickerModal } from './InstancePickerModal';
import { BROKEN_PLATE, BROKEN_LABEL, BrokenRefNote } from './BrokenRef';
import { Tooltip } from '@/shared/ui/Tooltip';

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
   * пикер заново — при том что снятие уже стёрло, на что смотреть при выборе замены. Значок и
   * подпись — как у составного поля (<code>ComplexFieldGroup</code>), чтобы одно и то же действие
   * везде называлось одинаково.</p>
   */
  function replaceButton(tone: string) {
    return (
      <Tooltip content="Заменить ссылку" name>
        <button type="button" onClick={() => setPickerOpen(true)}
          className={`p-1 transition-colors shrink-0 ${tone}`}>
          <Icon icon={Refresh} size={18} />
        </button>
      </Tooltip>
    );
  }

  if (iRef) {
    const inst = otherInstances.find(i => i.id === iRef.instanceId);
    const dt = inst ? allDocTypes.find(t => t.id === inst.documentTypeId) : undefined;
    const label = inst?.name || dt?.name || iRef.displayName;
    // Instance-промах ловим локально и бесплатно (данные комплекта уже в руках) — без похода в backend.
    const isBroken = broken || !inst;
    if (isBroken) {
      return (
        <div>
          <div className={`flex items-center gap-2 rounded-md px-3 py-2 ${BROKEN_PLATE}`}>
            <Icon icon={Description} size={18} className="text-error shrink-0" />
            <span className={`flex-1 min-w-0 text-sm font-medium truncate ${BROKEN_LABEL}`}>{iRef.displayName}</span>
            {replaceButton('text-error hover:text-on-surface')}
            <Tooltip content="Снять ссылку" name>
              <button type="button" onClick={() => onChange(null)}
                className="p-1 text-error hover:text-on-surface transition-colors"><Icon icon={LinkOff} size={18} /></button>
            </Tooltip>
          </div>
          <BrokenRefNote />
          {picker}
        </div>
      );
    }
    return (
      <div className="flex items-center gap-2 border border-primary/30 rounded-md px-3 py-2 bg-primary-container">
        <Icon icon={Description} size={18} className="text-primary shrink-0" />
        <span className="flex-1 min-w-0">
          <span className="block text-sm text-on-primary-container font-medium truncate">{label}</span>
          {inst?.name && dt && <span className="block text-xs text-on-primary-container/70 truncate">{dt.name}</span>}
        </span>
        {inst && (
          <span className={`text-xs px-1.5 py-0.5 rounded-xs font-medium shrink-0 ${STATUS_COLORS[inst.status]}`}>
            {STATUS_LABELS[inst.status]}
          </span>
        )}
        {replaceButton('text-on-primary-container/70 hover:text-on-primary-container')}
        <Tooltip content="Снять ссылку" name>
          <button type="button" onClick={() => onChange(null)}
            className="p-1 text-on-primary-container/70 hover:text-error transition-colors">
            <Icon icon={LinkOff} size={18} />
          </button>
        </Tooltip>
        {picker}
      </div>
    );
  }

  if (cRef) {
    if (broken) {
      return (
        <div>
          <div className={`flex items-center gap-2 rounded-md px-3 py-2 ${BROKEN_PLATE}`}>
            <Icon icon={Description} size={18} className="text-error shrink-0" />
            <span className={`flex-1 min-w-0 text-sm font-medium truncate ${BROKEN_LABEL}`}>{cRef.displayName}</span>
            {replaceButton('text-error hover:text-on-surface')}
            <Tooltip content="Снять ссылку" name>
              <button type="button" onClick={() => onChange(null)}
                className="p-1 text-error hover:text-on-surface transition-colors"><Icon icon={LinkOff} size={18} /></button>
            </Tooltip>
          </div>
          <BrokenRefNote />
          {picker}
        </div>
      );
    }
    return (
      <div className="flex items-center gap-2 border border-outline-variant rounded-md px-3 py-2 bg-warning-container">
        <Icon icon={Description} size={18} className="text-warning shrink-0" />
        <span className="flex-1 min-w-0">
          <span className="block text-sm text-warning font-medium truncate">{cRef.displayName}</span>
          <span className="block text-xs text-warning truncate">Общие данные</span>
        </span>
        {replaceButton('text-warning hover:text-primary')}
        <Tooltip content="Снять ссылку" name>
          <button type="button" onClick={() => onChange(null)}
            className="p-1 text-warning hover:text-error transition-colors">
            <Icon icon={LinkOff} size={18} />
          </button>
        </Tooltip>
        {picker}
      </div>
    );
  }

  return (
    <>
      <button type="button" onClick={() => setPickerOpen(true)}
        className="flex items-center gap-2 px-3 py-2 text-sm text-primary border border-dashed border-primary/40 rounded-md hover:bg-primary-container transition-colors w-full">
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
        <span className="text-xs font-medium text-on-surface-variant">
          Документы <span className="text-on-surface-variant">{items.length} ссылок</span>
        </span>
        <button type="button" onClick={() => setPickerOpen(true)}
          className="flex items-center gap-1 text-xs text-primary px-2 py-0.5 rounded-xs hover:bg-primary-container transition-colors">
          <Icon icon={Add} size={18} /> Добавить
        </button>
      </div>
      {items.length === 0 ? (
        <p className="text-xs text-on-surface-variant text-center py-3">Нет ссылок — нажмите «Добавить»</p>
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
                  <div className={`flex items-center gap-2 px-3 py-2 ${BROKEN_PLATE}`}>
                    <Icon icon={Description} size={18} className="text-error shrink-0" />
                    <span className={`flex-1 min-w-0 text-sm truncate ${BROKEN_LABEL}`}>{ref.displayName}</span>
                    <button type="button" onClick={() => removeRef(i)}
                      className="p-1 text-error hover:text-on-surface shrink-0"><Icon icon={Delete} size={18} /></button>
                  </div>
                  <BrokenRefNote compact />
                </div>
              );
            }
            return (
              <div key={i} className="flex items-center gap-2 px-3 py-2">
                <Icon icon={Description} size={18} className={isCatalog ? 'text-warning shrink-0' : 'text-primary shrink-0'} />
                <span className="flex-1 min-w-0">
                  <span className="block text-sm text-on-surface-variant truncate">{label}</span>
                  {isCatalog ? (
                    <span className="block text-xs text-warning truncate">Общие данные</span>
                  ) : inst?.name && dt ? (
                    <span className="block text-xs text-on-surface-variant truncate">{dt.name}</span>
                  ) : null}
                </span>
                {inst && (
                  <span className={`text-xs px-1.5 py-0.5 rounded-xs font-medium shrink-0 ${STATUS_COLORS[inst.status]}`}>
                    {STATUS_LABELS[inst.status]}
                  </span>
                )}
                <button type="button" onClick={() => removeRef(i)}
                  className="p-1 text-on-surface-variant hover:text-error shrink-0">
                  <Icon icon={Delete} size={18} />
                </button>
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
