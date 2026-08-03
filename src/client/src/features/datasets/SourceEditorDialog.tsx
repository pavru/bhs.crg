import { useState, useEffect, useMemo } from 'react';
import { Plus, Trash2 } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { Select, SelectItem } from '@/shared/ui/Select';
import { TextField } from '@/shared/ui/TextField';
import { useCreateDataSetSource, useUpdateDataSetSource, useListZipXmlEntries, useSourceCandidates } from '@/shared/api/datasets';
import { XPathBuilder } from './xpath/XPathBuilder';
import { JsonPathBuilder } from './jsonpath/JsonPathBuilder';
import { DATA_SET_FORMAT_LABELS } from '@/shared/api/types';
import type { ColumnExprDef, DataSetSource, DataSetFormat } from '@/shared/api/types';

/** С этого числа кандидатов список перестаёт читаться целиком — показываем поиск. */
const CANDIDATE_SEARCH_FROM = 7;

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
  const isSystem = format === 'System';
  // PDF-источник — проекция из СЫРЬЯ распознанного набора (issue #40), системный — консолидация
  // данных системы (issue #580). И то и другое выбирается из готового списка, как лист в Excel:
  // никаких XPath/колонок, поэтому редактор у них общий.
  const picksCandidate = isPdf || isSystem;
  const usesCandidates = tabular || picksCandidate;
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

  // Готовые кандидаты — без нераспознанных таблиц (issue #385): их создать нельзя, пока таблица
  // не распознана (это делается в списке кандидатов под источниками кнопкой «Распознать таблицу»).
  // useMemo не для скорости: без него массив новый на каждый рендер, и эффект подстановки ниже
  // перезапускается бесконечно, переписывая выбор пользователя.
  const readyCandidates = useMemo(() => candidates.filter(c => c.firstPageIndex == null), [candidates]);
  // Список кандидатов длинный только у общих данных (issue #627) — там их столько же, сколько
  // составных типов; у остальных форматов их единицы, и поиск был бы лишним полем в диалоге.
  const [candidateQuery, setCandidateQuery] = useState('');
  const shownCandidates = useMemo(() => {
    const q = candidateQuery.trim().toLowerCase();
    return q ? readyCandidates.filter(c => c.name.toLowerCase().includes(q)) : readyCandidates;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [candidates, candidateQuery]);
  const pendingTableCount = candidates.length - readyCandidates.length;

  /**
   * Новый источник из кандидата (лист/«весь файл»/PDF-проекция/консолидация): подставить первый
   * ПОКАЗАННЫЙ и его имя.
   *
   * Именно показанный, а не первый вообще (issue #627): поиск сужает список, и выбранное могло из
   * него выпасть. Оставить прежний выбор значило бы сохранить не то, что человек видит — он ищет
   * «Приказ», в списке пусто, а сохраняется «Единица измерения», подставленная при открытии.
   * Имя обновляем, только пока оно совпадает с именем какого-то кандидата: набранное руками —
   * решение пользователя, и переписывать его нельзя.
   */
  useEffect(() => {
    if (initial || !usesCandidates || shownCandidates.length === 0) return;
    const first = shownCandidates[0];
    setSheetOrPath(prev => (prev && shownCandidates.some(c => c.sheetOrPath === prev)) ? prev : first.sheetOrPath);
    setName(prev => (!prev || readyCandidates.some(c => c.name === prev)) ? first.name : prev);
  }, [initial, usesCandidates, shownCandidates, readyCandidates]);

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

    if (picksCandidate) {
      if (!sheetOrPath.trim()) { setError('Выберите данные для источника'); return; }
      finalSheetOrPath = sheetOrPath.trim(); // маркер кандидата (gost-cover/…/system:set-documents)
      finalColumns = null;                    // готовая проекция/консолидация — колонки известны
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
    : `Новый источник (${isZip ? 'XML в архиве' : DATA_SET_FORMAT_LABELS[format]})`;

  // Выбирать не из чего — сохранять нечего: кнопка «Сохранить» осталась бы активной и молча
  // упиралась бы в ошибку валидации (issue #606).
  const nothingToPick = picksCandidate && !initial && readyCandidates.length === 0;

  return (
    <Modal open onOpenChange={open => { if (!open) onClose(); }} title={dialogTitle} wide
      footer={
        <div className="flex justify-end gap-2">
          <Button type="button" variant="text" onClick={onClose}>Отмена</Button>
          <Button type="button" variant="filled" onClick={handleSave} loading={isPending}
            disabled={nothingToPick}>
            {isPending ? 'Сохранение…' : 'Сохранить'}
          </Button>
        </div>
      }>
      <div className="space-y-4 min-w-[520px]">
        {!nothingToPick && (
          <TextField label="Название" value={name} onChange={e => setName(e.target.value)}
            hint="Позиции спецификации" />
        )}

        {picksCandidate ? (
          <div>
            {/* Пустой выпадающий список выглядел бы поломкой — когда выбирать не из чего,
                показываем причину вместо самого поля (issue #606). */}
            {readyCandidates.length === 0 ? (
              <p className="text-sm text-fg3">
                {isSystem
                  ? 'Система пока не может ничего консолидировать на этом уровне: либо всё уже добавлено, либо здесь таких данных нет — «Документы комплекта», например, доступны только у набора внутри комплекта.'
                  : pendingTableCount > 0
                    ? 'Все проекции набора уже добавлены. Таблицы документов сначала распознайте — кнопка «Распознать таблицу» в списке кандидатов под источниками.'
                    : candidates.length === 0
                      ? 'Нет доступных проекций. Если набор ещё не распознан — запустите «Распознать»; таблицы документов распознаются отдельно (кнопка ✂ разбиения).'
                      : 'Все проекции набора уже добавлены как источники.'}
              </p>
            ) : (
              <>
                {/* Поиск появляется, когда список перестаёт читаться глазами (issue #627): общих
                    данных столько же, сколько составных типов, и выпадающий список из десятков
                    строк заставляет прокручивать вместо того, чтобы назвать нужное. */}
                {readyCandidates.length > CANDIDATE_SEARCH_FROM && (
                  <TextField label="Поиск консолидации" value={candidateQuery}
                    onChange={e => setCandidateQuery(e.target.value)}
                    hint={`Показано ${shownCandidates.length} из ${readyCandidates.length}`} />
                )}
                <Select label={isSystem ? 'Данные системы' : 'Данные набора'} value={sheetOrPath || undefined} placeholder="— выберите —"
                  onValueChange={v => { setSheetOrPath(v); const c = candidates.find(x => x.sheetOrPath === v); if (c) setName(prev => prev || c.name); }}>
                  {shownCandidates.map(c => (
                    <SelectItem key={c.sheetOrPath} value={c.sheetOrPath}>
                      {c.name} · {c.rowCount} строк{c.warning ? ' · с оговоркой' : ''}
                    </SelectItem>
                  ))}
                </Select>
                {shownCandidates.length === 0 && (
                  <p className="text-xs text-fg4 mt-1">Под поиск ничего не подходит.</p>
                )}
              </>
            )}
            {/* Оговорку показываем ДО создания источника: узнать о неполноте данных, уже привязав
                их к полям документа, — узнать поздно. */}
            {selectedCandidate?.warning && (
              <p className="text-xs text-warning mt-1">{selectedCandidate.warning}</p>
            )}
            {selectedCandidate && selectedCandidate.columns.length > 0 && (
              <p className="text-xs text-fg4 mt-1">Колонки: {selectedCandidate.columns.join(', ')}</p>
            )}
          </div>
        ) : tabular ? (
          needsSheet ? (
            <div>
              <Select label="Лист" value={sheetOrPath || undefined} placeholder="— выберите лист —"
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
                <Select label="Файл в архиве" value={entryPath || undefined}
                  placeholder="— выберите XML-файл в архиве —"
                  onValueChange={setEntryPath} className="font-mono">
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
