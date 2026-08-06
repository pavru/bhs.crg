import { useMemo, useState } from 'react';
import { ChevronDown, ChevronRight, Plus, X, AlertTriangle } from 'lucide-react';
import { MappingEditor } from '@/features/document-sets/editor/DataSetsTab';
import { parseSourceColumnNames } from '@/shared/api/datasetHelpers';
import type { SchemaField } from '@/shared/api/schema';
import type { DataSetSource, DocumentType, MaterializeDiscriminator } from '@/shared/api/types';

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
        <label className="flex-1">
          <span className="block text-xs font-medium mb-1 text-fg3">Колонка-признак</span>
          <select
            value={discriminator.column}
            onChange={e => setDiscriminator({ column: e.target.value })}
            className="w-full border border-stroke rounded-md px-2 py-1.5 text-sm bg-surface text-fg1"
          >
            <option value="">— не выбрана —</option>
            {columnNames.map(c => <option key={c} value={c}>{c}</option>)}
          </select>
        </label>
        <label className="w-56">
          <span className="block text-xs font-medium mb-1 text-fg3">Читать как</span>
          <select
            value={discriminator.kind}
            onChange={e => setDiscriminator({ kind: e.target.value as MaterializeDiscriminator['kind'] })}
            className="w-full border border-stroke rounded-md px-2 py-1.5 text-sm bg-surface text-fg1"
          >
            <option value="docTypeCode">код типа документа</option>
            <option value="docId">идентификатор документа</option>
          </select>
        </label>
      </div>

      <div className="rounded-lg border border-stroke divide-y divide-stroke">
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
                className="w-full flex items-center gap-2 px-2 py-1.5 text-left hover:bg-base transition-colors"
              >
                {isOpen ? <ChevronDown size={13} className="text-fg4 shrink-0" /> : <ChevronRight size={13} className="text-fg4 shrink-0" />}
                <span className={`text-xs font-medium ${disabled ? 'text-fg4' : 'text-fg2'}`}>{v.title}</span>
                <span className="flex-1 flex flex-wrap gap-1">
                  {ids.map(id => (
                    <span key={id}
                      className={`inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[11px] border ${
                        conflicting.has(id)
                          ? 'bg-red-50 text-danger border-red-200'
                          : 'bg-base text-fg3 border-stroke'}`}>
                      {typeName(id)}
                      <X size={10} className="cursor-pointer hover:text-danger"
                        onClick={e => { e.stopPropagation(); setRules(v.key, ids.filter(x => x !== id)); }} />
                    </span>
                  ))}
                </span>
                {disabled && <span className="text-[11px] text-fg4 shrink-0">выключен, строки пропускаются</span>}
                {needsMapping && (
                  <span className="text-[11px] text-danger shrink-0 flex items-center gap-1">
                    <AlertTriangle size={11} /> нет маппинга
                  </span>
                )}
              </button>

              {isOpen && (
                <div className="px-3 pb-3 space-y-2">
                  <div className="flex items-center gap-2">
                    <Plus size={12} className="text-fg4 shrink-0" />
                    <select
                      value=""
                      onChange={e => { if (e.target.value) setRules(v.key, [...ids, e.target.value]); }}
                      className="flex-1 border border-stroke rounded px-2 py-1 text-xs bg-surface text-fg1"
                    >
                      <option value="">— добавить тип документа —</option>
                      {allDocTypes
                        .filter(t => t.kind === 'Document' && !t.isAbstract && !ids.includes(t.id))
                        .map(t => <option key={t.id} value={t.id}>{t.name}</option>)}
                    </select>
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

/**
 * Почему настройку нельзя сохранять; null — можно.
 *
 * Та же логика живёт на сервере и там же остаётся авторитетом: здесь она нужна, чтобы отказ пришёл
 * до нажатия, а не после. Дублирование намеренное и односторонне безопасное — клиент строже или
 * равен серверу, но никогда не мягче.
 */
export function discriminatorProblem(
  variants: SchemaField[],
  mapping: Record<string, string>,
  discriminator: MaterializeDiscriminator,
  typeName: (id: string) => string,
): string | null {
  if (!discriminator.column) return 'Выберите колонку, по которой определяется вариант.';

  // Владельца запоминаем ЗАГОЛОВКОМ варианта, а не ключом: сообщение читает человек, и «АОСР_ЭМ»
  // вместо «АОСР электромонтаж» заставляет искать соответствие руками.
  const owners = new Map<string, string>();
  for (const v of variants) {
    const ids = discriminator.rules[v.key] ?? [];
    if (ids.length > 0 && !mapping[v.key])
      return `Для варианта «${v.title}» назначены типы документов, но не задан маппинг колонок.`;
    for (const id of ids) {
      const other = owners.get(id);
      if (other && other !== v.title)
        return `Тип документа «${typeName(id)}» назначен сразу двум вариантам — «${other}» и «${v.title}».`;
      owners.set(id, v.title);
    }
  }

  if (owners.size === 0) return 'Ни одному варианту не назначено ни одного типа документа.';
  return null;
}
