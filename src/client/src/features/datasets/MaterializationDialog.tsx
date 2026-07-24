import { useEffect, useState } from 'react';
import { Info } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { dtCard, dtTable, dtTh, dtTd, dtRow } from '@/shared/ui/dataTable';
import { TypePickerField } from '@/shared/ui/TypePickerField';
import type { PickType } from '@/shared/ui/TypePicker';
import { useListDocumentTypes } from '@/shared/api/documentTypes';
import { useSetMaterialization, useMaterializePreview } from '@/shared/api/datasets';
import { MappingEditor } from '@/features/document-sets/editor/DataSetsTab';
import { VariantSegmentedSwitch } from '@/features/document-sets/fields/ComplexFields';
import { resolveEffectiveFields } from '@/shared/api/schema';
import { FUNCTIONAL_TAG } from '@/shared/api/tags';
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

  // Union-тип (issue #320/#391): «заполняется ровно один вариант» — маппим один активный вариант,
  // а не все поля union разом. Материализатор кладёт один ключ на строку → корректный union-экземпляр.
  const isUnion = !!selectedType
    && ((selectedType.schema as { tags?: string[] }).tags ?? []).includes(FUNCTIONAL_TAG.typeUnion);
  const presentVariant = isUnion ? effectiveFields.find(f => mapping[f.key])?.key : undefined;
  const firstVariant = effectiveFields[0]?.key ?? '';
  const [activeVariant, setActiveVariant] = useState<string>('');
  // Стэш неактивных вариантов — недеструктивное переключение (как в UnionFieldGroup): persist хранит
  // ОДИН ключ, токен другого варианта живёт в локальном стэше до закрытия диалога.
  const [variantStash, setVariantStash] = useState<Record<string, string>>({});
  // Подхватываем активный вариант из загруженного маппинга / при смене типа сбрасываем на первый.
  useEffect(() => {
    if (!isUnion) return;
    setActiveVariant(presentVariant ?? firstVariant);
  }, [isUnion, presentVariant, firstVariant]);

  function switchVariant(key: string) {
    if (key === activeVariant) return;
    const curToken = mapping[activeVariant];
    setVariantStash(prev => ({ ...prev, [activeVariant]: curToken ?? '' }));
    const restored = variantStash[key];
    setMapping(restored ? { [key]: restored } : {}); // union: ровно один ключ
    setActiveVariant(key);
  }

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
            onChange={id => { setTypeId(id ?? ''); setMapping({}); setVariantStash({}); }} />
        </div>

        {selectedType && (
          effectiveFields.length === 0 ? (
            <p className="text-xs text-warning">У типа «{selectedType.name}» нет полей — задайте поля типу, чтобы было куда маппить.</p>
          ) : (
            <div className="rounded-lg border border-stroke p-3 space-y-3">
              {isUnion && (
                <div className="flex items-center justify-between gap-2">
                  <VariantSegmentedSwitch
                    options={effectiveFields.map(f => ({ key: f.key, label: f.title, filled: !!mapping[f.key] || !!variantStash[f.key] }))}
                    active={activeVariant}
                    onSelect={switchVariant}
                  />
                  <span className="text-[11px] text-fg4 flex items-center gap-1 shrink-0"
                    title="Заполняется ровно один из вариантов">
                    <Info size={11} /> заполните одно из
                  </span>
                </div>
              )}
              <MappingEditor
                source={source}
                schemaFields={isUnion ? effectiveFields.filter(f => f.key === activeVariant) : effectiveFields}
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
