import { useEffect, useMemo, useState } from 'react';
import { Info } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { dtCard, dtTable, dtTh, dtTd, dtRow } from '@/shared/ui/dataTable';
import { TypePickerField } from '@/shared/ui/TypePickerField';
import type { PickType } from '@/shared/ui/TypePicker';
import { useListDocumentTypes } from '@/shared/api/documentTypes';
import { useSetMaterialization, useMaterializePreview } from '@/shared/api/datasets';
import { MappingEditor } from '@/features/document-sets/editor/DataSetsTab';
import { VariantPicker } from '@/features/document-sets/fields/ComplexFields';
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

  // Модель предпросмотра (issue #393): разворачиваем вложенные объекты в колонки-листья (столбец на
  // лист = прямое «колонка источника → поле»); single-child unwrap разматывает общий верхний ключ
  // (кейс union — поля варианта показываются напрямую, имя — подписью над таблицей); двухэтажная шапка.
  const previewModel = useMemo(
    () => buildPreviewModel(
      (preview.data?.rows ?? []) as Record<string, unknown>[],
      key => effectiveFields.find(f => f.key === key)?.title,
    ),
    [preview.data, effectiveFields],
  );
  const previewHasGroups = previewModel.groups.some(g => g.parentKey !== '');

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

        <TypePickerField className="w-full" label="Тип для материализации" title="Тип для материализации"
          placeholder="— не материализовать —" clearable={{ label: 'Не материализовать' }}
          recentKey="materialize-type"
          types={allDocTypes.filter(t => !t.isAbstract).map<PickType>(t => ({
            id: t.id, name: t.name, code: t.code,
            section: t.kind === 'Composite' ? 'Составные типы' : 'Типы документов',
          }))}
          value={typeId || undefined}
          onChange={id => { setTypeId(id ?? ''); setMapping({}); setVariantStash({}); }} />

        {selectedType && (
          effectiveFields.length === 0 ? (
            <p className="text-xs text-warning">У типа «{selectedType.name}» нет полей — задайте поля типу, чтобы было куда маппить.</p>
          ) : (
            <div className="rounded-lg border border-stroke p-3 space-y-3">
              {isUnion && (
                <div className="space-y-1.5">
                  <span className="text-[11px] text-fg4 flex items-center gap-1"
                    title="Заполняется ровно один из вариантов">
                    <Info size={11} /> выберите вариант — заполняется ровно один
                  </span>
                  <VariantPicker
                    options={effectiveFields.map(f => ({ key: f.key, label: f.title, filled: !!mapping[f.key] || !!variantStash[f.key] }))}
                    active={activeVariant}
                    onSelect={switchVariant}
                  />
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
                allowDocRef
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
                  previewModel.leaves.length === 0 ? (
                    <p className="text-xs text-fg4 p-3">Нет заполненных полей — задайте маппинг.</p>
                  ) : (
                    <>
                      {previewModel.unwrappedLabel && (
                        <p className="text-[11px] text-fg4 px-2 pt-2">Вариант: <span className="text-fg2">{previewModel.unwrappedLabel}</span></p>
                      )}
                      <table className={dtTable}>
                        <thead>
                          {/* Верхний этаж: группы-родители (colSpan) + листья-без-родителя (rowSpan 2, если есть вложенность). */}
                          <tr>
                            {previewModel.groups.map(g => g.parentKey === ''
                              ? g.leaves.map(l => (
                                  <th key={l.key} rowSpan={previewHasGroups ? 2 : 1} className={dtTh} title={l.key}>{leafLabel(l)}</th>
                                ))
                              : (
                                <th key={g.parentKey} colSpan={g.leaves.length} className={`${dtTh} text-center`} title={g.parentKey}>
                                  {lastSeg(g.parentKey)}
                                </th>
                              ))}
                          </tr>
                          {/* Нижний этаж: только листья сгруппированных родителей (нет вложенности — нет ряда). */}
                          {previewHasGroups && (
                            <tr>
                              {previewModel.groups.filter(g => g.parentKey !== '').flatMap(g =>
                                g.leaves.map(l => <th key={l.key} className={dtTh} title={l.key}>{leafLabel(l)}</th>))}
                            </tr>
                          )}
                        </thead>
                        <tbody>
                          {previewModel.rows.map((row, i) => (
                            <tr key={i} className={dtRow}>
                              {previewModel.leaves.map(l => (
                                <td key={l.key} className={`${dtTd} text-fg2 align-top`}>{renderCell(getPath(row, l.path))}</td>
                              ))}
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </>
                  )
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
  // Массивы/doc-array — сводкой (issue #393): «одна материализованная строка = один ряд», не разворачиваем.
  if (Array.isArray(v)) return <span className="text-fg4">▦ {v.length} элем.</span>;
  if (typeof v === 'object') return <span className="text-fg4">{JSON.stringify(v)}</span>;
  return String(v);
}

// ── Модель предпросмотра: разворот вложенного в колонки-листья (issue #393) ────────────────────
export interface LeafCol { path: string[]; key: string }
export interface LeafGroup { parentKey: string; leaves: LeafCol[] }
export interface PreviewModel {
  rows: Record<string, unknown>[];
  leaves: LeafCol[];
  groups: LeafGroup[];
  /** Подпись размотанного верхнего ключа (кейс union) над таблицей, либо '' . */
  unwrappedLabel: string;
}

/** Собирает модель предпросмотра из сырых материализованных строк (чистая, тестируемая). */
export function buildPreviewModel(
  rawRows: Record<string, unknown>[],
  titleFor: (key: string) => string | undefined = () => undefined,
): PreviewModel {
  const { rows, unwrapped } = unwrapSingleChild(rawRows);
  // Порядок листьев — натуральный (DFS): у согласованных строк материализатора соседи-поля одного
  // родителя уже идут подряд и совпадают с порядком полей типа (что и ждёт пользователь).
  const leaves = collectLeaves(rows);
  const unwrappedLabel = unwrapped
    .map((seg, i) => (i === 0 ? titleFor(seg) ?? seg : seg))
    .join(' → ');
  return { rows, leaves, groups: coalesceByParent(leaves), unwrappedLabel };
}

/** Составной объект, разворачиваемый в колонки (не null, не массив, не файл-вложение). */
function isPlainObject(v: unknown): v is Record<string, unknown> {
  return v != null && typeof v === 'object' && !Array.isArray(v) && !isFileAttachment(v);
}

/** Пока ВСЕ строки лежат под одним общим верхним ключом-объектом — разматываем его (кейс union:
 * поля варианта наружу, имя ключа — в подпись над таблицей). Возвращает размотанные строки + цепочку ключей. */
function unwrapSingleChild(rows: Record<string, unknown>[]): { rows: Record<string, unknown>[]; unwrapped: string[] } {
  const unwrapped: string[] = [];
  let cur = rows;
  while (cur.length > 0) {
    const keys0 = Object.keys(cur[0] ?? {});
    if (keys0.length !== 1) break;
    const k = keys0[0];
    if (!cur.every(r => {
      const ks = Object.keys(r ?? {});
      return ks.length === 1 && ks[0] === k && isPlainObject(r[k]);
    })) break;
    unwrapped.push(k);
    cur = cur.map(r => r[k] as Record<string, unknown>);
  }
  return { rows: cur, unwrapped };
}

/** Обходит строки, собирая пути листьев (значения-НЕ-объекты) в порядке первого появления. */
function collectLeaves(rows: Record<string, unknown>[]): LeafCol[] {
  const seen = new Set<string>();
  const cols: LeafCol[] = [];
  function walk(obj: Record<string, unknown>, prefix: string[]) {
    for (const [k, v] of Object.entries(obj)) {
      const path = [...prefix, k];
      if (isPlainObject(v)) walk(v, path);
      else {
        const key = path.join('.');
        if (!seen.has(key)) { seen.add(key); cols.push({ path, key }); }
      }
    }
  }
  for (const r of rows) walk(r ?? {}, []);
  return cols;
}

/** Схлопывает подряд идущие листья с общим родителем в группы (родитель '' = лист без родителя). */
function coalesceByParent(leaves: LeafCol[]): LeafGroup[] {
  const groups: LeafGroup[] = [];
  for (const l of leaves) {
    const pk = l.path.slice(0, -1).join('.');
    const last = groups[groups.length - 1];
    if (last && last.parentKey === pk) last.leaves.push(l);
    else groups.push({ parentKey: pk, leaves: [l] });
  }
  return groups;
}

export function getPath(row: Record<string, unknown>, path: string[]): unknown {
  let cur: unknown = row;
  for (const seg of path) {
    if (!isPlainObject(cur)) return undefined;
    cur = cur[seg];
  }
  return cur;
}

const leafLabel = (l: LeafCol) => l.path[l.path.length - 1];
const lastSeg = (key: string) => key.split('.').pop() ?? key;
