import { useState } from 'react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { dtCard, dtTable, dtTh, dtTd, dtRow } from '@/shared/ui/dataTable';
import { TypePickerField } from '@/shared/ui/TypePickerField';
import type { PickType } from '@/shared/ui/TypePicker';
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
  // Live-превью по ТЕКУЩИМ (несохранённым) типу+маппингу (issue #294): обновляется на каждую правку.
  const preview = useMaterializePreview(source.id, typeId || undefined, mapping, showPreview && !!typeId);

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
          <TypePickerField className="w-full" aria-label="Тип для материализации" title="Тип для материализации"
            placeholder="— не материализовать —" clearable={{ label: 'Не материализовать' }}
            recentKey="materialize-type"
            types={allDocTypes.filter(t => !t.isAbstract).map<PickType>(t => ({
              id: t.id, name: t.name, code: t.code,
              section: t.kind === 'Composite' ? 'Составные типы' : 'Типы документов',
            }))}
            value={typeId || undefined}
            onChange={id => { setTypeId(id ?? ''); setMapping({}); }} />
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

        {typeId && (
          <div>
            <button type="button" onClick={() => setShowPreview(v => !v)}
              className="text-xs text-brand hover:text-brand-hover">
              {showPreview ? 'Скрыть предпросмотр' : 'Предпросмотр материализации'}
            </button>
            {showPreview && (
              <div className={`mt-2 ${dtCard} max-h-72`}>
                {preview.isLoading ? (
                  <p className="text-xs text-fg4 p-3">Загрузка…</p>
                ) : preview.data?.error ? (
                  <p className="text-xs text-danger p-3">{preview.data.error}</p>
                ) : preview.data && preview.data.rows.length > 0 ? (
                  <table className={dtTable}>
                    <thead>
                      <tr>
                        {previewCols.map(c => <th key={c} className={dtTh}>{c}</th>)}
                      </tr>
                    </thead>
                    <tbody>
                      {preview.data.rows.map((row, i) => (
                        <tr key={i} className={dtRow}>
                          {previewCols.map(c => (
                            <td key={c} className={`${dtTd} text-fg2 align-top`}>{renderCell(row[c])}</td>
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
