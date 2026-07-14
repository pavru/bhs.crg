import { useState, useEffect } from 'react';
import { Plus, Trash2 } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { Select, SelectItem } from '@/shared/ui/Select';
import { useCreateDataSetSource, useUpdateDataSetSource, useListZipXmlEntries, useSourceCandidates } from '@/shared/api/datasets';
import { XPathBuilder } from './xpath/XPathBuilder';
import { JsonPathBuilder } from './jsonpath/JsonPathBuilder';
import type { ColumnExprDef, DataSetSource, DataSetFormat } from '@/shared/api/types';

type PathFormat = 'Xml' | 'Json';

const DEFAULT_ROW_SELECTOR: Record<PathFormat, string> = { Xml: '/Root/Item', Json: '$.items[*]' };

/** Табличные форматы: extraction = лист (XLSX/XLS) или весь файл (CSV); колонки — автоматически. */
function isTabularFormat(f: DataSetFormat): boolean {
  return f === 'Csv' || f === 'Xls' || f === 'Xlsx';
}

/** Для ZIP-архивов sheetOrPath хранится как "путь/в/архиве.xml::/Row/Selector" (см. ZipDataSetParser). */
function splitZipPath(sheetOrPath: string | undefined, isZip: boolean, format: PathFormat): { entryPath: string; rowSelector: string } {
  if (!isZip) return { entryPath: '', rowSelector: sheetOrPath ?? DEFAULT_ROW_SELECTOR[format] };
  if (!sheetOrPath) return { entryPath: '', rowSelector: DEFAULT_ROW_SELECTOR[format] };
  const idx = sheetOrPath.indexOf('::');
  return idx < 0
    ? { entryPath: sheetOrPath, rowSelector: DEFAULT_ROW_SELECTOR[format] }
    : { entryPath: sheetOrPath.slice(0, idx), rowSelector: sheetOrPath.slice(idx + 2) };
}

/**
 * Явное создание/редактирование источника на основе сырого набора (issue #20). Форма формат-зависимая:
 * - CSV — только имя (весь файл, extraction тривиальна);
 * - XLS/XLSX — имя + выбор листа (подсказки листов из детекта, `sheetOrPath` = имя листа);
 * - XML/JSON/ZIP — row-selector (XPath/JSONPath-builder) + список колонок (каждая — относительный путь).
 * Колонки для табличных форматов определяются автоматически по заголовку.
 */
export function SourceEditorDialog({ fileId, format, initial, onClose }: {
  fileId: string;
  format: DataSetFormat;
  initial?: DataSetSource;
  onClose: () => void;
}) {
  const tabular = isTabularFormat(format);
  const isZip = format === 'Zip';
  const isPdf = format === 'Pdf';
  // PDF-источник — проекция из СЫРЬЯ распознанного набора (issue #40): выбор кандидата
  // (Обложка/Титул/Документы), как выбор листа в Excel. Никаких XPath/колонок.
  const usesCandidates = tabular || isPdf;
  const needsSheet = format === 'Xls' || format === 'Xlsx';
  const pathFormat: PathFormat = format === 'Json' ? 'Json' : 'Xml';
  const PathBuilder = pathFormat === 'Json' ? JsonPathBuilder : XPathBuilder;

  const initialSplit = splitZipPath(initial?.sheetOrPath, isZip, pathFormat);
  const [name, setName] = useState(initial?.name ?? '');
  const [entryPath, setEntryPath] = useState(initialSplit.entryPath);
  // В табличном режиме sheetOrPath — имя листа (XLSX) / «весь файл» (CSV); в path-режиме — row-selector.
  const [sheetOrPath, setSheetOrPath] = useState(tabular ? (initial?.sheetOrPath ?? '') : initialSplit.rowSelector);
  const [columns, setColumns] = useState<ColumnExprDef[]>(() => {
    try { return initial?.columnExpressions ? JSON.parse(initial.columnExpressions) : []; }
    catch { return []; }
  });
  const [error, setError] = useState('');

  const { data: zipEntries = [] } = useListZipXmlEntries(isZip ? fileId : undefined);
  // Кандидаты (листы/«весь файл») — подсказки для табличных форматов в режиме создания.
  const { data: candidates = [] } = useSourceCandidates(usesCandidates ? fileId : undefined);
  const create = useCreateDataSetSource();
  const update = useUpdateDataSetSource();
  const isPending = create.isPending || update.isPending;

  // Новый источник из кандидата (лист/«весь файл»/PDF-проекция): как только приедут кандидаты —
  // подставить первый и его имя.
  useEffect(() => {
    if (initial || !usesCandidates || candidates.length === 0) return;
    setSheetOrPath(prev => prev || candidates[0].sheetOrPath);
    setName(prev => prev || candidates[0].name);
  }, [initial, usesCandidates, candidates]);

  // Текущее значение может отсутствовать в списке (архив обновился) — не терять его молча.
  const entryOptions = entryPath && !zipEntries.includes(entryPath) ? [entryPath, ...zipEntries] : zipEntries;
  const selectedCandidate = candidates.find(c => c.sheetOrPath === sheetOrPath);

  // Полный row-selector с учётом ZIP-адресации — для предпросмотра (null = ещё не готов, напр.
  // архив без выбранного файла). Колонки предпросматриваются относительно него же.
  function composeRowSelector(rs: string): string | null {
    if (!rs.trim()) return null;
    if (!isZip) return rs;
    return entryPath.trim() ? `${entryPath.trim()}::${rs.trim()}` : null;
  }

  function addColumn() {
    setColumns(prev => [...prev, { name: '', expr: '' }]);
  }
  function updateColumn(i: number, patch: Partial<ColumnExprDef>) {
    setColumns(prev => prev.map((c, idx) => idx === i ? { ...c, ...patch } : c));
  }
  function removeColumn(i: number) {
    setColumns(prev => prev.filter((_, idx) => idx !== i));
  }

  async function handleSave() {
    setError('');
    if (!name.trim()) { setError('Укажите название'); return; }

    let finalSheetOrPath: string;
    let finalColumns: ColumnExprDef[] | null;

    if (isPdf) {
      if (!sheetOrPath.trim()) { setError('Выберите данные набора для источника'); return; }
      finalSheetOrPath = sheetOrPath.trim(); // маркер кандидата (gost-cover/gost-titlepage/gost-documents)
      finalColumns = null;                    // проекция из группировки, колонки готовы
    } else if (tabular) {
      if (needsSheet && !sheetOrPath.trim()) { setError('Выберите лист'); return; }
      // CSV — весь файл: extraction тривиальна, sheetOrPath из кандидата (обычно "default").
      finalSheetOrPath = sheetOrPath.trim() || candidates[0]?.sheetOrPath || 'default';
      finalColumns = null; // колонки определяются автоматически по заголовку
    } else {
      if (isZip && !entryPath.trim()) { setError('Выберите файл внутри архива'); return; }
      if (!sheetOrPath.trim()) { setError('Укажите row-selector (путь к строкам)'); return; }
      finalSheetOrPath = isZip ? `${entryPath.trim()}::${sheetOrPath.trim()}` : sheetOrPath.trim();
      const clean = columns.filter(c => c.name.trim() && c.expr.trim());
      finalColumns = clean.length ? clean : null;
    }

    try {
      if (initial) {
        await update.mutateAsync({ id: initial.id, name: name.trim(), sheetOrPath: finalSheetOrPath, columnExpressions: finalColumns });
      } else {
        await create.mutateAsync({ fileId, name: name.trim(), sheetOrPath: finalSheetOrPath, columnExpressions: finalColumns });
      }
      onClose();
    } catch (e: unknown) {
      const msg = (e as { response?: { data?: { error?: string } } })?.response?.data?.error;
      setError(msg ?? (e instanceof Error ? e.message : 'Ошибка сохранения'));
    }
  }

  const dialogTitle = initial
    ? 'Редактировать источник'
    : `Новый источник (${isZip ? 'XML в архиве' : format})`;

  return (
    <Modal open onOpenChange={open => { if (!open) onClose(); }} title={dialogTitle} wide
      footer={
        <div className="flex justify-end gap-2">
          <Button type="button" variant="text" onClick={onClose}>Отмена</Button>
          <Button type="button" variant="filled" onClick={handleSave} loading={isPending}>
            {isPending ? 'Сохранение…' : 'Сохранить'}
          </Button>
        </div>
      }>
      <div className="space-y-4 min-w-[520px]">
        <div>
          <label className="block text-sm font-medium text-fg1 mb-1">Название</label>
          <input value={name} onChange={e => setName(e.target.value)}
            placeholder={tabular ? 'Позиции спецификации' : 'Позиции спецификации'}
            className="w-full px-3 py-2 rounded-lg border border-stroke-strong bg-surface text-sm" />
        </div>

        {isPdf ? (
          <div>
            <label className="block text-sm font-medium text-fg1 mb-1">Данные набора</label>
            <Select value={sheetOrPath || undefined} placeholder="— выберите —" aria-label="Данные набора"
              onValueChange={v => { setSheetOrPath(v); const c = candidates.find(x => x.sheetOrPath === v); if (c) setName(prev => prev || c.name); }}>
              {candidates.map(c => (
                <SelectItem key={c.sheetOrPath} value={c.sheetOrPath}>{c.name} · {c.rowCount} строк</SelectItem>
              ))}
            </Select>
            {candidates.length === 0 && (
              <p className="text-xs text-fg4 mt-1">Набор ещё не распознан — сначала запустите «Распознать».</p>
            )}
            {selectedCandidate && selectedCandidate.columns.length > 0 && (
              <p className="text-xs text-fg4 mt-1">Колонки: {selectedCandidate.columns.join(', ')}</p>
            )}
          </div>
        ) : tabular ? (
          needsSheet ? (
            <div>
              <label className="block text-sm font-medium text-fg1 mb-1">Лист</label>
              <Select value={sheetOrPath || undefined} placeholder="— выберите лист —" aria-label="Лист"
                onValueChange={setSheetOrPath}>
                {candidates.map(c => (
                  <SelectItem key={c.sheetOrPath} value={c.sheetOrPath}>{c.name} · {c.rowCount} строк</SelectItem>
                ))}
              </Select>
              {candidates.length === 0 && (
                <p className="text-xs text-fg4 mt-1">Листы не обнаружены.</p>
              )}
              {selectedCandidate && selectedCandidate.columns.length > 0 && (
                <p className="text-xs text-fg4 mt-1">Колонки: {selectedCandidate.columns.join(', ')}</p>
              )}
            </div>
          ) : (
            <p className="text-xs text-fg4">
              Весь файл — колонки определяются автоматически по заголовку
              {candidates[0]?.columns.length ? `: ${candidates[0].columns.join(', ')}` : ''}.
            </p>
          )
        ) : (
          <>
            {isZip && (
              <div>
                <label className="block text-sm font-medium text-fg1 mb-1">Файл в архиве</label>
                <Select value={entryPath || undefined} placeholder="— выберите XML-файл в архиве —"
                  aria-label="Файл в архиве" onValueChange={setEntryPath} className="font-mono">
                  {entryOptions.map(p => <SelectItem key={p} value={p}>{p}</SelectItem>)}
                </Select>
                {entryOptions.length === 0 && (
                  <p className="text-xs text-fg4 mt-1">В архиве не найдено XML-файлов.</p>
                )}
              </div>
            )}

            <div>
              <label className="block text-sm font-medium text-fg1 mb-1">
                Row-selector — путь к строкам (одна строка = один узел; условия сужают до конкретных)
              </label>
              <PathBuilder value={sheetOrPath} onChange={setSheetOrPath} placeholder={DEFAULT_ROW_SELECTOR[pathFormat]}
                preview={v => { const rs = composeRowSelector(v); return rs ? { fileId, rowSelector: rs } : null; }} />
            </div>

            <div className="border-t border-stroke pt-3">
              <div className="flex items-center justify-between mb-2">
                <p className="text-sm font-medium text-fg1">
                  Колонки <span className="text-xs font-normal text-fg4">— относительно узла строки</span>
                </p>
                <button type="button" onClick={addColumn}
                  className="flex items-center gap-1 text-xs text-brand hover:text-brand-hover">
                  <Plus size={13} /> Колонка
                </button>
              </div>
              {columns.length === 0 && (
                <p className="text-xs text-fg4 py-1">
                  Колонок нет — будут определены автоматически по дочерним элементам/атрибутам строки.
                </p>
              )}
              <div className="space-y-2">
                {columns.map((col, i) => (
                  <div key={i} className="flex items-start gap-2">
                    <input value={col.name} onChange={e => updateColumn(i, { name: e.target.value })}
                      placeholder="Название колонки"
                      className="w-40 shrink-0 px-2 py-1.5 rounded-md border border-stroke-strong bg-surface text-sm" />
                    <div className="flex-1 min-w-0">
                      <PathBuilder value={col.expr} onChange={expr => updateColumn(i, { expr })}
                        placeholder={pathFormat === 'Json' ? 'id или name или info.code' : '@id или Name или Info/Code'}
                        preview={v => { const rs = composeRowSelector(sheetOrPath); return rs && v.trim() ? { fileId, rowSelector: rs, expr: v } : null; }} />
                    </div>
                    <button type="button" onClick={() => removeColumn(i)}
                      className="p-1.5 text-fg4 hover:text-danger shrink-0">
                      <Trash2 size={14} />
                    </button>
                  </div>
                ))}
              </div>
            </div>
          </>
        )}

        {error && <p className="text-sm text-danger">{error}</p>}
      </div>
    </Modal>
  );
}
