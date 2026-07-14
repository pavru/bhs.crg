import { useState } from 'react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { useListDocumentTypes } from '@/shared/api/documentTypes';
import { useSetMaterialization, useMaterializePreview } from '@/shared/api/datasets';
import { MappingEditor } from '@/features/document-sets/editor/DataSetsTab';
import { resolveEffectiveFields } from '@/shared/api/schema';
import { isFileAttachment, formatBytes } from '@/shared/api/attachments';
import type { DataSetSource } from '@/shared/api/types';

/**
 * Материализация источника в тип (issue #19): пользователь выбирает тип (составной/документ) и
 * маппинг колонок → поля типа ОДИН РАЗ на источнике. Дальше поля документов, чьи тип совместим,
 * ссылаются на этот источник без маппинга (тип↔тип). Материализация — после всех обработок.
 */
export function MaterializationDialog({ source, onClose }: { source: DataSetSource; onClose: () => void }) {
  const { data: allDocTypes = [] } = useListDocumentTypes();
  const [typeId, setTypeId] = useState(source.materializeTypeId ?? '');
  const [mapping, setMapping] = useState<Record<string, string>>(source.materializeMapping ?? {});
  const [showPreview, setShowPreview] = useState(false);
  const save = useSetMaterialization();

  const selectedType = allDocTypes.find(t => t.id === typeId);
  const effectiveFields = selectedType ? resolveEffectiveFields(selectedType, allDocTypes) : [];
  const preview = useMaterializePreview(source.id, showPreview && !!source.materializeTypeId);

  function handleSave() {
    save.mutate(
      { sourceId: source.id, typeId: typeId || null, mapping: typeId ? mapping : null },
      { onSuccess: onClose },
    );
  }

  const previewCols = preview.data
    ? [...new Set(preview.data.rows.flatMap(r => Object.keys(r)))]
    : [];

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title={`Материализация источника «${source.name}»`} wide
      footer={
        <div className="flex justify-end gap-2">
          <Button type="button" variant="text" onClick={onClose}>Отмена</Button>
          <Button type="button" variant="filled" onClick={handleSave} loading={save.isPending}>
            {save.isPending ? 'Сохранение…' : 'Сохранить'}
          </Button>
        </div>
      }>
      <div className="space-y-4 min-w-[560px]">
        <p className="text-xs text-fg4">
          Источник разворачивает каждую строку (после всех обработок) в сущность выбранного типа.
          Маппинг задаётся здесь один раз — поля документов совместимого типа ссылаются на источник без маппинга.
        </p>

        <div>
          <label className="block text-sm font-medium text-fg1 mb-1">Тип для материализации</label>
          <select value={typeId} onChange={e => { setTypeId(e.target.value); setMapping({}); setShowPreview(false); }}
            className="w-full px-3 py-2 rounded-lg border border-stroke-strong bg-surface text-sm">
            <option value="">— не материализовать —</option>
            {allDocTypes.filter(t => !t.isAbstract).map(t => (
              <option key={t.id} value={t.id}>{t.name} ({t.kind === 'Composite' ? 'составной' : 'документ'})</option>
            ))}
          </select>
        </div>

        {selectedType && (
          effectiveFields.length === 0 ? (
            <p className="text-xs text-warning">У типа «{selectedType.name}» нет полей — задайте поля типу, чтобы было куда маппить.</p>
          ) : (
            <div className="rounded-lg border border-stroke p-3">
              <MappingEditor
                source={source}
                schemaFields={effectiveFields}
                tabularFields={[]}
                allDocTypes={allDocTypes}
                mapping={mapping}
                targetFieldKey={null}
                onChange={m => setMapping(m)}
                hideModeSelector
              />
            </div>
          )
        )}

        {source.materializeTypeId && (
          <div>
            <button type="button" onClick={() => setShowPreview(v => !v)}
              className="text-xs text-brand hover:text-brand-hover">
              {showPreview ? 'Скрыть предпросмотр' : 'Предпросмотр материализации'}
            </button>
            {showPreview && (
              <div className="mt-2 rounded-lg border border-stroke overflow-auto max-h-72">
                {preview.isLoading ? (
                  <p className="text-xs text-fg4 p-3">Загрузка…</p>
                ) : preview.data?.error ? (
                  <p className="text-xs text-danger p-3">{preview.data.error}</p>
                ) : preview.data && preview.data.rows.length > 0 ? (
                  <table className="text-xs w-full">
                    <thead>
                      <tr className="bg-base">
                        {previewCols.map(c => <th key={c} className="text-left px-2 py-1 font-medium text-fg3">{c}</th>)}
                      </tr>
                    </thead>
                    <tbody>
                      {preview.data.rows.map((row, i) => (
                        <tr key={i} className="border-t border-stroke">
                          {previewCols.map(c => (
                            <td key={c} className="px-2 py-1 text-fg2 align-top">{renderCell(row[c])}</td>
                          ))}
                        </tr>
                      ))}
                    </tbody>
                  </table>
                ) : (
                  <p className="text-xs text-fg4 p-3">Нет строк.</p>
                )}
                {preview.data && <p className="text-[11px] text-fg4 px-2 py-1">Всего строк: {preview.data.totalRows}</p>}
              </div>
            )}
          </div>
        )}
        {!source.materializeTypeId && typeId && (
          <p className="text-[11px] text-fg4">Сохраните материализацию, чтобы увидеть предпросмотр.</p>
        )}
      </div>
    </Modal>
  );
}

function renderCell(v: unknown) {
  if (v == null) return <span className="text-fg4">—</span>;
  if (isFileAttachment(v)) return <span>{v.fileName} <span className="text-fg4">({formatBytes(v.size)})</span></span>;
  if (typeof v === 'object') return <span className="text-fg4">{JSON.stringify(v)}</span>;
  return String(v);
}
