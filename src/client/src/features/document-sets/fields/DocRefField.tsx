import { useState } from 'react';
import { FileText, Plus, Trash2, Unlink } from 'lucide-react';
import type { CatalogScope, DocumentInstance, DocumentType, FieldRef } from '@/shared/api/types';
import { isFieldRef, isInstanceRef } from '@/shared/api/types';
import type { SchemaField } from '@/shared/api/schema';
import { STATUS_COLORS, STATUS_LABELS } from './constants';
import { InstancePickerModal } from './InstancePickerModal';
import { BROKEN_PLATE, BROKEN_LABEL, BrokenRefNote } from './BrokenRef';

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

  if (iRef) {
    const inst = otherInstances.find(i => i.id === iRef.instanceId);
    const dt = inst ? allDocTypes.find(t => t.id === inst.documentTypeId) : undefined;
    const label = inst?.name || dt?.name || iRef.displayName;
    // Instance-промах ловим локально и бесплатно (данные комплекта уже в руках) — без похода в backend.
    const isBroken = broken || !inst;
    if (isBroken) {
      return (
        <div>
          <div className={`flex items-center gap-2 rounded-lg px-3 py-2 ${BROKEN_PLATE}`}>
            <FileText size={14} className="text-danger shrink-0" />
            <span className={`flex-1 min-w-0 text-sm font-medium truncate ${BROKEN_LABEL}`}>{iRef.displayName}</span>
            <button type="button" onClick={() => setPickerOpen(true)} title="Выбрать другой документ"
              className="p-1 text-danger hover:text-fg1 transition-colors"><FileText size={13} /></button>
            <button type="button" onClick={() => onChange(null)} title="Снять ссылку"
              className="p-1 text-danger hover:text-fg1 transition-colors"><Unlink size={13} /></button>
          </div>
          <BrokenRefNote />
          <InstancePickerModal open={pickerOpen} onOpenChange={setPickerOpen}
            field={field} allDocTypes={allDocTypes} otherInstances={otherInstances}
            setId={setId} onSelect={ref => onChange(ref)} />
        </div>
      );
    }
    return (
      <div className="flex items-center gap-2 border border-indigo-200 rounded-lg px-3 py-2 bg-indigo-50">
        <FileText size={14} className="text-indigo-500 shrink-0" />
        <span className="flex-1 min-w-0">
          <span className="block text-sm text-indigo-700 font-medium truncate">{label}</span>
          {inst?.name && dt && <span className="block text-xs text-indigo-400 truncate">{dt.name}</span>}
        </span>
        {inst && (
          <span className={`text-xs px-1.5 py-0.5 rounded font-medium shrink-0 ${STATUS_COLORS[inst.status]}`}>
            {STATUS_LABELS[inst.status]}
          </span>
        )}
        <button type="button" onClick={() => onChange(null)} title="Снять ссылку"
          className="p-1 text-indigo-400 hover:text-danger transition-colors">
          <Unlink size={13} />
        </button>
      </div>
    );
  }

  if (cRef) {
    if (broken) {
      return (
        <div>
          <div className={`flex items-center gap-2 rounded-lg px-3 py-2 ${BROKEN_PLATE}`}>
            <FileText size={14} className="text-danger shrink-0" />
            <span className={`flex-1 min-w-0 text-sm font-medium truncate ${BROKEN_LABEL}`}>{cRef.displayName}</span>
            <button type="button" onClick={() => onChange(null)} title="Снять ссылку"
              className="p-1 text-danger hover:text-fg1 transition-colors"><Unlink size={13} /></button>
          </div>
          <BrokenRefNote />
        </div>
      );
    }
    return (
      <div className="flex items-center gap-2 border border-warning-border rounded-lg px-3 py-2 bg-warning-subtle">
        <FileText size={14} className="text-warning shrink-0" />
        <span className="flex-1 min-w-0">
          <span className="block text-sm text-warning font-medium truncate">{cRef.displayName}</span>
          <span className="block text-xs text-warning truncate">Общие данные</span>
        </span>
        <button type="button" onClick={() => onChange(null)} title="Снять ссылку"
          className="p-1 text-warning hover:text-danger transition-colors">
          <Unlink size={13} />
        </button>
      </div>
    );
  }

  return (
    <>
      <button type="button" onClick={() => setPickerOpen(true)}
        className="flex items-center gap-2 px-3 py-2 text-sm text-indigo-600 hover:text-indigo-700 border border-dashed border-indigo-300 rounded-lg hover:bg-indigo-50 transition-colors w-full">
        <FileText size={13} /> Выбрать документ...
      </button>
      <InstancePickerModal open={pickerOpen} onOpenChange={setPickerOpen}
        field={field} allDocTypes={allDocTypes} otherInstances={otherInstances}
        setId={setId} onSelect={ref => onChange(ref)} />
    </>
  );
}

export function DocArrayField({ field, allDocTypes, value, onChange, otherInstances, setId, brokenPaths, basePath }: {
  field: SchemaField; allDocTypes: DocumentType[]; value: unknown;
  onChange: (val: unknown[]) => void; otherInstances: DocumentInstance[];
  setId?: string;
  /** Пути битых ссылок (issue #332) + базовый путь этого массива — для пометки конкретных элементов. */
  brokenPaths?: Set<string>; basePath?: string;
}) {
  const [pickerOpen, setPickerOpen] = useState(false);
  const items = (Array.isArray(value) ? value : []).filter(isFieldRef);
  const linkedInstanceIds = new Set(items.map(r => r.instanceId).filter(Boolean));

  function addRef(ref: FieldRef) {
    if (ref.instanceId && linkedInstanceIds.has(ref.instanceId)) return;
    onChange([...items, ref]);
  }

  function removeRef(idx: number) {
    onChange(items.filter((_, i) => i !== idx));
  }

  return (
    <div className="border border-stroke rounded-lg overflow-hidden">
      <div className="flex items-center justify-between px-3 py-2 bg-base border-b border-stroke">
        <span className="text-xs font-medium text-fg3">
          Документы <span className="text-fg4">{items.length} ссылок</span>
        </span>
        <button type="button" onClick={() => setPickerOpen(true)}
          className="flex items-center gap-1 text-xs text-indigo-600 hover:text-indigo-700 px-2 py-0.5 rounded hover:bg-indigo-100 transition-colors">
          <Plus size={11} /> Добавить
        </button>
      </div>
      {items.length === 0 ? (
        <p className="text-xs text-fg4 text-center py-3">Нет ссылок — нажмите «Добавить»</p>
      ) : (
        <div className="divide-y divide-muted">
          {items.map((ref, i) => {
            const isCatalog = ref.$ref === 'catalog';
            const inst = !isCatalog ? otherInstances.find(inst => inst.id === ref.instanceId) : undefined;
            const dt = inst ? allDocTypes.find(t => t.id === inst.documentTypeId) : undefined;
            const label = inst?.name || dt?.name || ref.displayName;
            // Битый элемент: instance-промах ловим локально; catalog — по backend-пути `basePath[i]`.
            const itemBroken = (!isCatalog && !inst)
              || (!!basePath && !!brokenPaths?.has(`${basePath}[${i}]`));
            if (itemBroken) {
              return (
                <div key={i}>
                  <div className={`flex items-center gap-2 px-3 py-2 ${BROKEN_PLATE}`}>
                    <FileText size={13} className="text-danger shrink-0" />
                    <span className={`flex-1 min-w-0 text-sm truncate ${BROKEN_LABEL}`}>{ref.displayName}</span>
                    <button type="button" onClick={() => removeRef(i)}
                      className="p-1 text-danger hover:text-fg1 shrink-0"><Trash2 size={13} /></button>
                  </div>
                  <BrokenRefNote compact />
                </div>
              );
            }
            return (
              <div key={i} className="flex items-center gap-2 px-3 py-2">
                <FileText size={13} className={isCatalog ? 'text-warning shrink-0' : 'text-indigo-400 shrink-0'} />
                <span className="flex-1 min-w-0">
                  <span className="block text-sm text-fg2 truncate">{label}</span>
                  {isCatalog ? (
                    <span className="block text-xs text-warning truncate">Общие данные</span>
                  ) : inst?.name && dt ? (
                    <span className="block text-xs text-fg4 truncate">{dt.name}</span>
                  ) : null}
                </span>
                {inst && (
                  <span className={`text-xs px-1.5 py-0.5 rounded font-medium shrink-0 ${STATUS_COLORS[inst.status]}`}>
                    {STATUS_LABELS[inst.status]}
                  </span>
                )}
                <button type="button" onClick={() => removeRef(i)}
                  className="p-1 text-fg4 hover:text-danger shrink-0">
                  <Trash2 size={13} />
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
