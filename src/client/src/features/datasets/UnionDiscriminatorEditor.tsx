import { useMemo, useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Add, ChevronRight, Close, KeyboardArrowDown, Warning } from '@/shared/ui/icons';
import { MappingEditor } from '@/features/document-sets/editor/DataSetsTab';
import { parseSourceColumnNames } from '@/shared/api/datasetHelpers';
import type { SchemaField } from '@/shared/api/schema';
import type { DataSetSource, DocumentType, MaterializeDiscriminator } from '@/shared/api/types';
import { Select, SelectItem } from '@/shared/ui/Select';

/**
 * Настройка «вариант по типу документа строки» (issue #716).
 *
 * Аккордеон, а не привычный VariantPicker: радио-семантика здесь ЛОЖНА. Picker означает «активен
 * ровно один», а в этом режиме настроены все варианты сразу — активный выбирается для каждой строки
 * отдельно, при генерации. Оставить радио значило бы показывать неправду о том, как всё работает.
 *
 * Типы у варианта — chips с «×», видимым ВСЕГДА, а не по наведению: удаление, спрятанное до hover,
 * уже стоило потери данных (см. память о безопасности удаления), да и на планшете hover'а нет.
 */
export function UnionDiscriminatorEditor({
  source, variants, allDocTypes, mapping, discriminator, onChange,
}: {
  source: DataSetSource;
  /** Поля union'а — его варианты. */
  variants: SchemaField[];
  allDocTypes: DocumentType[];
  mapping: Record<string, string>;
  discriminator: MaterializeDiscriminator;
  onChange: (mapping: Record<string, string>, discriminator: MaterializeDiscriminator) => void;
}) {
  const [open, setOpen] = useState<string>(variants[0]?.key ?? '');

  const columnNames = useMemo(() => {
    const computed = (source.computedColumns ?? []).map(c => c.alias).filter(Boolean);
    return [...new Set([...parseSourceColumnNames(source.cachedSchema), ...computed])];
  }, [source.cachedSchema, source.computedColumns]);

  const typeName = (id: string) => allDocTypes.find(t => t.id === id)?.name ?? id;

  // Тип, назначенный двум вариантам, — противоречие: «кто первый» решал бы порядок ключей в JSON.
  const conflicting = useMemo(() => {
    const owners = new Map<string, string>();
    const bad = new Set<string>();
    for (const [variant, ids] of Object.entries(discriminator.rules)) {
      for (const id of ids) {
        if (owners.has(id) && owners.get(id) !== variant) bad.add(id);
        else owners.set(id, variant);
      }
    }
    return bad;
  }, [discriminator.rules]);

  function setDiscriminator(patch: Partial<MaterializeDiscriminator>) {
    onChange(mapping, { ...discriminator, ...patch });
  }

  function setRules(variantKey: string, ids: string[]) {
    const rules = { ...discriminator.rules };
    if (ids.length > 0) rules[variantKey] = ids;
    else delete rules[variantKey];
    onChange(mapping, { ...discriminator, rules });
  }

  function setVariantMapping(variantKey: string, next: Record<string, string>) {
    const merged = { ...mapping };
    // Ключи чужих вариантов не трогаем — редактор варианта видит только своё поле.
    if (next[variantKey]) merged[variantKey] = next[variantKey];
    else delete merged[variantKey];
    onChange(merged, discriminator);
  }

  return (
    <div className="space-y-3">
      <div className="flex items-end gap-2">
        <Select label="Колонка-признак" containerClassName="flex-1" value={discriminator.column}
          onValueChange={column => setDiscriminator({ column })} emptyItem="— не выбрана —">
          {columnNames.map(c => <SelectItem key={c} value={c}>{c}</SelectItem>)}
        </Select>
        <Select label="Читать как" containerClassName="w-56" value={discriminator.kind}
          onValueChange={kind => setDiscriminator({ kind: kind as MaterializeDiscriminator['kind'] })}>
          <SelectItem value="docTypeCode">код типа документа</SelectItem>
          <SelectItem value="docId">идентификатор документа</SelectItem>
        </Select>
      </div>

      <div className="rounded-md border border-outline-variant divide-y divide-outline-variant">
        {variants.map(v => {
          const ids = discriminator.rules[v.key] ?? [];
          const isOpen = open === v.key;
          const disabled = ids.length === 0;
          const needsMapping = ids.length > 0 && !mapping[v.key];

          return (
            <div key={v.key}>
              <button
                type="button"
                onClick={() => setOpen(isOpen ? '' : v.key)}
                className="w-full flex items-center gap-2 px-2 py-1.5 text-left state-layer"
              >
                {isOpen ? <Icon icon={KeyboardArrowDown} size={18} className="text-on-surface-variant shrink-0" /> : <Icon icon={ChevronRight} size={18} className="text-on-surface-variant shrink-0" />}
                <span className={`text-body-small font-medium ${disabled ? 'text-on-surface-variant' : 'text-on-surface-variant'}`}>{v.title}</span>
                <span className="flex-1 flex flex-wrap gap-1">
                  {ids.map(id => (
                    <span key={id}
                      className={`inline-flex items-center gap-1 px-1.5 py-0.5 rounded-xs text-label-small border ${
                        conflicting.has(id)
                          ? 'bg-error-container text-on-error-container border-error/30'
                          : 'bg-surface-container-low text-on-surface-variant border-outline-variant'}`}>
                      {typeName(id)}
                      <Icon icon={Close} size={18} className="cursor-pointer hover:text-error"
                        onClick={e => { e.stopPropagation(); setRules(v.key, ids.filter(x => x !== id)); }} />
                    </span>
                  ))}
                </span>
                {disabled && <span className="text-label-small text-on-surface-variant shrink-0">выключен, строки пропускаются</span>}
                {needsMapping && (
                  <span className="text-label-small text-error shrink-0 flex items-center gap-1">
                    <Icon icon={Warning} size={18} /> нет маппинга
                  </span>
                )}
              </button>

              {isOpen && (
                <div className="px-3 pb-3 space-y-2">
                  <div className="flex items-center gap-2">
                    <Icon icon={Add} size={18} className="text-on-surface-variant shrink-0" />
                    <Select size="sm" className="flex-1" aria-label="Добавить тип документа"
                      value="" placeholder="— добавить тип документа —"
                      onValueChange={id => { if (id) setRules(v.key, [...ids, id]); }}>
                      {/* Абстрактные типы В СПИСКЕ — и это не оплошность. Правило на тип забирает
                          всех его потомков, а общий предок как раз обычно и абстрактен; без него
                          пришлось бы перечислять каждый конкретный подвид руками, и появившийся
                          завтра тихо выпал бы из реестра — ровно то, чего правило по предку
                          избегает (об этом же говорит USER_GUIDE). */}
                      {allDocTypes
                        .filter(t => t.kind === 'Document' && !ids.includes(t.id))
                        .map(t => (
                          <SelectItem key={t.id} value={t.id}>
                            {t.isAbstract ? `${t.name} (базовый — со всеми подвидами)` : t.name}
                          </SelectItem>
                        ))}
                    </Select>
                  </div>
                  <MappingEditor
                    source={source}
                    schemaFields={variants.filter(f => f.key === v.key)}
                    tabularFields={[]}
                    allDocTypes={allDocTypes}
                    mapping={mapping}
                    targetFieldKey={null}
                    onChange={m => setVariantMapping(v.key, m)}
                    hideModeSelector
                    allowDocRef
                  />
                </div>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}
