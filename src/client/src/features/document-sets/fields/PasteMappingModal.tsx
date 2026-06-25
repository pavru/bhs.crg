import { useState, useEffect } from 'react';
import { Modal } from '@/shared/ui/Modal';
import type { CommonDataEntry, DocumentType, FieldRef } from '@/shared/api/types';
import { resolveEffectiveFields, isSubtypeOf, type SchemaField } from '@/shared/api/schema';
// ─── Paste mapping modal ──────────────────────────────────────────────────────

export function PasteMappingModal({
  open, onOpenChange, initialText, tableFields, allDocTypes, commonDataEntries, onApply,
}: {
  open: boolean; onOpenChange: (v: boolean) => void;
  initialText: string;
  tableFields: SchemaField[];
  allDocTypes: DocumentType[];
  commonDataEntries: CommonDataEntry[];
  onApply: (rows: Record<string, unknown>[]) => void;
}) {
  const [step, setStep] = useState<'input' | 'map'>('input');
  const [rawText, setRawText] = useState('');
  const [skipHeader, setSkipHeader] = useState(false);
  const [colMappings, setColMappings] = useState<string[]>([]);
  const [matchFields, setMatchFields] = useState<Record<string, string>>({});

  function initMapping(text: string) {
    const rows = text.trim().split('\n').map(r => r.split('\t'));
    const maxCols = rows.length > 0 ? Math.max(...rows.map(r => r.length)) : 0;
    const firstRow = rows[0] ?? [];
    const isHeader = firstRow.some(cell =>
      tableFields.some(f =>
        f.title.toLowerCase() === cell.trim().toLowerCase() ||
        f.key.toLowerCase() === cell.trim().toLowerCase()
      )
    );
    setSkipHeader(isHeader);
    setColMappings(Array.from({ length: maxCols }, (_, i) => {
      if (!isHeader) return '';
      const header = (firstRow[i] ?? '').trim().toLowerCase();
      return tableFields.find(f =>
        f.title.toLowerCase() === header || f.key.toLowerCase() === header
      )?.key ?? '';
    }));
    setMatchFields({});
  }

  useEffect(() => {
    if (!open) return;
    setRawText(initialText);
    if (initialText.trim()) { initMapping(initialText); setStep('map'); }
    else setStep('input');
  }, [open]); // eslint-disable-line react-hooks/exhaustive-deps

  const allRows = rawText.trim() ? rawText.trim().split('\n').map(r => r.split('\t')) : [];
  const maxCols = allRows.length > 0 ? Math.max(...allRows.map(r => r.length)) : 0;
  const dataRows = skipHeader ? allRows.slice(1) : allRows;
  const importCount = dataRows.filter(r => r.some(c => c.trim())).length;

  function apply() {
    const newRows = dataRows.filter(r => r.some(c => c.trim())).map(r => {
      const row: Record<string, unknown> = {};
      colMappings.forEach((fieldKey, ci) => {
        if (!fieldKey) return;
        const field = tableFields.find(f => f.key === fieldKey);
        if (!field) return;
        const raw = (r[ci] ?? '').trim();
        if (!raw) return;
        if (field.type === 'complex') {
          const mf = matchFields[fieldKey];
          if (!mf) return;
          const compositeType = allDocTypes.find(dt => dt.id === field.typeId) ?? null;
          const entry = commonDataEntries.find(e => {
            if (compositeType && !isSubtypeOf(e.compositeTypeId, compositeType.id, allDocTypes)) return false;
            return String(e.data[mf] ?? '').toLowerCase().trim() === raw.toLowerCase().trim();
          });
          if (entry) row[fieldKey] = { $ref: 'catalog', entryId: entry.id, displayName: entry.displayName, scope: entry.scope } as FieldRef;
        } else if (field.type === 'number') {
          const n = parseFloat(raw.replace(',', '.').replace(/\s/g, ''));
          if (!isNaN(n)) row[fieldKey] = n;
        } else if (field.type === 'boolean') {
          row[fieldKey] = ['1', 'да', 'true', 'yes', '+', 'y'].includes(raw.toLowerCase());
        } else if (field.type === 'enum') {
          const opts = (field.options ?? []).filter(o => o !== '');
          const match = opts.find(o => o.toLowerCase() === raw.toLowerCase());
          if (match) row[fieldKey] = match;
        } else if (field.type === 'date') {
          const m = /^(\d{1,2})[./\-](\d{1,2})[./\-](\d{4})$/.exec(raw);
          if (m) row[fieldKey] = `${m[3]}-${m[2].padStart(2, '0')}-${m[1].padStart(2, '0')}`;
          else row[fieldKey] = raw;
        } else {
          row[fieldKey] = raw;
        }
      });
      return row;
    });
    onApply(newRows);
    onOpenChange(false);
  }

  const selectCls = 'flex-1 min-w-[120px] border border-stroke-strong rounded px-2 py-1 text-xs bg-surface focus:outline-none focus-visible:ring-1 focus-visible:ring-brand';

  if (step === 'input') {
    return (
      <Modal open={open} onOpenChange={onOpenChange} title="Вставить из Excel" wide>
        <div className="space-y-4">
          <p className="text-sm text-fg2">Скопируйте ячейки в Excel и вставьте сюда (Ctrl+V):</p>
          <textarea autoFocus value={rawText} onChange={e => setRawText(e.target.value)}
            onPaste={e => {
              const text = e.clipboardData.getData('text/plain');
              if (text.trim()) { e.preventDefault(); setRawText(text); initMapping(text); setStep('map'); }
            }}
            placeholder="Вставьте данные из Excel..." rows={7}
            className="w-full border border-stroke-strong rounded-md px-3 py-2 text-xs font-mono focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface resize-none" />
          <div className="flex justify-end gap-3">
            <button type="button" onClick={() => onOpenChange(false)}
              className="px-4 py-2 text-sm text-fg2 hover:bg-muted rounded-md">Отмена</button>
            <button type="button" disabled={!rawText.trim()}
              onClick={() => { initMapping(rawText); setStep('map'); }}
              className="px-4 py-2 text-sm bg-brand text-white rounded-md disabled:opacity-50 hover:bg-brand-hover">
              Далее →
            </button>
          </div>
        </div>
      </Modal>
    );
  }

  // step === 'map'
  return (
    <Modal open={open} onOpenChange={onOpenChange} title="Сопоставление столбцов" wide>
      <div className="space-y-4">
        <div className="flex items-center gap-3">
          <label className="flex items-center gap-2 text-sm text-fg2 cursor-pointer">
            <input type="checkbox" checked={skipHeader} onChange={e => setSkipHeader(e.target.checked)}
              className="w-4 h-4 rounded border-stroke-strong text-brand" />
            Первая строка — заголовки
          </label>
          <span className="text-xs text-fg4">Найдено строк данных: {importCount}</span>
        </div>
        <div className="overflow-x-auto">
          <table className="w-full text-xs border-collapse">
            <thead>
              <tr className="bg-base">
                <th className="border border-stroke px-2 py-1.5 text-left text-fg3 font-medium">Столбец</th>
                <th className="border border-stroke px-2 py-1.5 text-left text-fg3 font-medium">Поле таблицы</th>
                <th className="border border-stroke px-2 py-1.5 text-left text-fg3 font-medium">Сопоставить по</th>
              </tr>
            </thead>
            <tbody>
              {Array.from({ length: maxCols }, (_, ci) => {
                const mappedKey = colMappings[ci] ?? '';
                const mappedField = tableFields.find(f => f.key === mappedKey);
                const firstRows = (skipHeader ? allRows.slice(1) : allRows).slice(0, 3);
                const preview = firstRows.map(r => r[ci] ?? '').filter(Boolean).join(', ');
                const needsMatch = mappedField?.type === 'complex';
                const compositeType = needsMatch
                  ? allDocTypes.find(dt => dt.id === mappedField?.typeId) ?? null : null;
                const matchableFields = compositeType
                  ? resolveEffectiveFields(compositeType, allDocTypes).filter(
                    f => f.type === 'string' || f.type === 'number',
                  ) : [];
                return (
                  <tr key={ci} className="hover:bg-base">
                    <td className="border border-stroke px-2 py-1.5">
                      <span className="font-mono text-fg2">
                        {skipHeader ? (allRows[0]?.[ci] ?? `Кол. ${ci + 1}`) : `Кол. ${ci + 1}`}
                      </span>
                      {preview && <span className="ml-2 text-fg4 truncate max-w-[120px] inline-block">{preview}</span>}
                    </td>
                    <td className="border border-stroke px-2 py-1.5">
                      <select value={mappedKey}
                        onChange={e => {
                          const next = [...colMappings];
                          next[ci] = e.target.value;
                          setColMappings(next);
                          setMatchFields(prev => { const n = { ...prev }; delete n[colMappings[ci]]; return n; });
                        }}
                        className={selectCls}>
                        <option value="">— пропустить —</option>
                        {tableFields.map(f => <option key={f.key} value={f.key}>{f.title}</option>)}
                      </select>
                    </td>
                    <td className="border border-stroke px-2 py-1.5">
                      {needsMatch && matchableFields.length > 0 && (
                        <select value={matchFields[mappedKey] ?? ''}
                          onChange={e => setMatchFields(prev => ({ ...prev, [mappedKey]: e.target.value }))}
                          className={selectCls}>
                          <option value="">— выберите поле —</option>
                          {matchableFields.map(f => <option key={f.key} value={f.key}>{f.title}</option>)}
                        </select>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
        <div className="flex justify-between pt-2 border-t border-stroke">
          <button type="button" onClick={() => setStep('input')}
            className="px-4 py-2 text-sm text-fg2 hover:bg-muted rounded-md">← Назад</button>
          <div className="flex gap-3">
            <button type="button" onClick={() => onOpenChange(false)}
              className="px-4 py-2 text-sm text-fg2 hover:bg-muted rounded-md">Отмена</button>
            <button type="button" onClick={apply} disabled={importCount === 0}
              className="px-4 py-2 text-sm bg-brand text-white rounded-md disabled:opacity-50 hover:bg-brand-hover">
              Импортировать {importCount > 0 ? `(${importCount} стр.)` : ''}
            </button>
          </div>
        </div>
      </div>
    </Modal>
  );
}

