import { useState, useEffect } from 'react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import type { CatalogScope, DocumentType, FieldRef } from '@/shared/api/types';
import { resolveObjectsBatch } from '@/shared/api/objects';
import { resolveEffectiveFields, type SchemaField } from '@/shared/api/schema';
// ─── Paste mapping modal ──────────────────────────────────────────────────────

/** Приведение скалярного значения ячейки к типу поля. undefined → пропустить (не парсится). */
function coerceScalar(field: SchemaField, raw: string): unknown {
  if (field.type === 'number') {
    const n = parseFloat(raw.replace(',', '.').replace(/\s/g, ''));
    return isNaN(n) ? undefined : n;
  }
  if (field.type === 'boolean') return ['1', 'да', 'true', 'yes', '+', 'y'].includes(raw.toLowerCase());
  if (field.type === 'enum') {
    const opts = (field.options ?? []).filter(o => o !== '');
    return opts.find(o => o.toLowerCase() === raw.toLowerCase());
  }
  if (field.type === 'date') {
    const m = /^(\d{1,2})[./\-](\d{1,2})[./\-](\d{4})$/.exec(raw);
    return m ? `${m[3]}-${m[2].padStart(2, '0')}-${m[1].padStart(2, '0')}` : raw;
  }
  return raw;
}

export function PasteMappingModal({
  open, onOpenChange, initialText, tableFields, allDocTypes, scope, scopeId, onApply,
}: {
  open: boolean; onOpenChange: (v: boolean) => void;
  initialText: string;
  tableFields: SchemaField[];
  allDocTypes: DocumentType[];
  /** Scope-контекст владельца — для серверного резолва «строка→объект» (issue #183). */
  scope?: CatalogScope; scopeId?: string | null;
  onApply: (rows: Record<string, unknown>[]) => void;
}) {
  const [step, setStep] = useState<'input' | 'map'>('input');
  const [rawText, setRawText] = useState('');
  const [skipHeader, setSkipHeader] = useState(false);
  // Сопоставление: ПОЛЕ таблицы → индекс колонки Excel-данных (источник). '' = пропустить.
  const [fieldCol, setFieldCol] = useState<Record<string, number>>({});
  // Для полей-ссылок (complex): по какому под-полю искать запись каталога.
  const [matchFields, setMatchFields] = useState<Record<string, string>>({});
  // Резолв идёт на сервере (issue #183): индикатор + промежуточная сводка перед вставкой.
  const [resolving, setResolving] = useState(false);
  const [pending, setPending] = useState<{ rows: Record<string, unknown>[]; linked: number; inline: number } | null>(null);

  const norm = (s: string) => s.trim().toLowerCase().replace(/\s+/g, ' ');

  // Авто-сопоставление по заголовкам первой строки.
  function mapByHeader(headerRow: string[], count: number): Record<string, number> {
    const m: Record<string, number> = {};
    tableFields.forEach(f => {
      const ci = headerRow.findIndex((cell, i) =>
        i < count && (norm(cell) === norm(f.title) || norm(cell) === norm(f.key)));
      if (ci >= 0) m[f.key] = ci;
    });
    return m;
  }

  function initMapping(text: string) {
    const rows = text.trim().split('\n').map(r => r.split('\t'));
    const maxCols = rows.length > 0 ? Math.max(...rows.map(r => r.length)) : 0;
    const firstRow = rows[0] ?? [];
    const isHeader = firstRow.some(cell => {
      const c = norm(cell);
      return !!c && tableFields.some(f => norm(f.title) === c || norm(f.key) === c);
    });
    setSkipHeader(isHeader);
    setFieldCol(isHeader ? mapByHeader(firstRow, maxCols) : {});
    setMatchFields({});
    setPending(null);
  }

  useEffect(() => {
    if (!open) return;
    setRawText(initialText);
    setPending(null);
    if (initialText.trim()) { initMapping(initialText); setStep('map'); }
    else setStep('input');
  }, [open]); // eslint-disable-line react-hooks/exhaustive-deps

  const allRows = rawText.trim() ? rawText.trim().split('\n').map(r => r.split('\t')) : [];
  const maxCols = allRows.length > 0 ? Math.max(...allRows.map(r => r.length)) : 0;
  const dataRows = skipHeader ? allRows.slice(1) : allRows;
  const importCount = dataRows.filter(r => r.some(c => c.trim())).length;

  function colLabel(ci: number): string {
    if (skipHeader) return (allRows[0]?.[ci] ?? '').trim() || `Кол. ${ci + 1}`;
    const preview = dataRows.slice(0, 3).map(r => r[ci] ?? '').filter(Boolean).join(', ');
    return preview ? `Кол. ${ci + 1} — ${preview}` : `Кол. ${ci + 1}`;
  }

  // Изменения сопоставления инвалидируют промежуточную сводку.
  function clearPending() { if (pending) setPending(null); }

  // Резолв строк на сервере → сборка строк. Complex-поля: match по выбранному под-полю (Field);
  // нет совпадения → inline-данные (сырой текст в это под-поле), чтобы НЕ терять ввод (не пусто).
  async function stage() {
    const dataOnly = dataRows.filter(r => r.some(c => c.trim()));
    const rows: Record<string, unknown>[] = dataOnly.map(() => ({}));
    const reqs: { row: number; fieldKey: string; mf: string; raw: string; typeId: string }[] = [];

    dataOnly.forEach((r, ri) => {
      Object.entries(fieldCol).forEach(([fieldKey, ci]) => {
        const field = tableFields.find(f => f.key === fieldKey);
        if (!field) return;
        const raw = (r[ci] ?? '').trim();
        if (!raw) return;
        if (field.type === 'complex') {
          const mf = matchFields[fieldKey];
          if (!mf || !field.typeId) return;
          reqs.push({ row: ri, fieldKey, mf, raw, typeId: field.typeId });
        } else {
          const v = coerceScalar(field, raw);
          if (v !== undefined) rows[ri][fieldKey] = v;
        }
      });
    });

    let linked = 0, inline = 0;
    setResolving(true);
    try {
      const results = await resolveObjectsBatch(scope, scopeId,
        reqs.map(q => ({ typeId: q.typeId, strategy: 'Field' as const, value: q.raw, fieldKey: q.mf })));
      reqs.forEach((q, i) => {
        const res = results[i];
        if (res) {
          rows[q.row][q.fieldKey] = { $ref: 'catalog', entryId: res.entryId, displayName: res.displayName ?? '', scope: res.scope } as FieldRef;
          linked++;
        } else {
          rows[q.row][q.fieldKey] = { [q.mf]: q.raw };
          inline++;
        }
      });
    } catch {
      // Ошибка резолва не должна терять ввод — вставляем всё как inline-данные.
      reqs.forEach(q => { rows[q.row][q.fieldKey] = { [q.mf]: q.raw }; });
      inline = reqs.length; linked = 0;
    } finally {
      setResolving(false);
    }

    if (inline === 0) { onApply(rows); onOpenChange(false); }
    else setPending({ rows, linked, inline }); // есть несопоставленные — показываем сводку перед вставкой
  }

  const selectCls = 'w-full min-w-[140px] border border-stroke-strong rounded px-2 py-1 text-xs bg-surface focus:outline-none focus-visible:ring-1 focus-visible:ring-brand';

  if (step === 'input') {
    return (
      <Modal open={open} onOpenChange={onOpenChange} title="Вставить из Excel" wide
        footer={
          <div className="flex justify-end gap-3">
            <Button variant="text" onClick={() => onOpenChange(false)}>Отмена</Button>
            <Button variant="filled" disabled={!rawText.trim()}
              onClick={() => { initMapping(rawText); setStep('map'); }}>
              Далее →
            </Button>
          </div>
        }>
        <div className="space-y-4">
          <p className="text-sm text-fg2">Скопируйте ячейки в Excel и вставьте сюда (Ctrl+V):</p>
          <textarea autoFocus value={rawText} onChange={e => setRawText(e.target.value)}
            onPaste={e => {
              const text = e.clipboardData.getData('text/plain');
              if (text.trim()) { e.preventDefault(); setRawText(text); initMapping(text); setStep('map'); }
            }}
            placeholder="Вставьте данные из Excel..." rows={7}
            className="w-full border border-stroke-strong rounded-md px-3 py-2 text-xs font-mono focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface resize-none" />
        </div>
      </Modal>
    );
  }

  // step === 'map', промежуточная сводка перед вставкой (есть несопоставленные строки).
  if (pending) {
    return (
      <Modal open={open} onOpenChange={onOpenChange} title="Готово к вставке" wide
        footer={
          <div className="flex justify-between">
            <Button variant="text" onClick={() => setPending(null)}>← Изменить сопоставление</Button>
            <div className="flex gap-3">
              <Button variant="text" onClick={() => onOpenChange(false)}>Отмена</Button>
              <Button variant="filled" onClick={() => { onApply(pending.rows); onOpenChange(false); }}>
                Вставить {importCount} стр.
              </Button>
            </div>
          </div>
        }>
        <div className="space-y-3 text-sm">
          <p className="text-fg2">Будет вставлено строк: <span className="font-medium text-fg1">{importCount}</span></p>
          <ul className="space-y-1.5">
            <li className="text-fg2">🔗 Связано с каталогом: <span className="font-medium text-fg1">{pending.linked}</span></li>
            <li className="text-fg2">
              📝 Вставлено как данные (совпадений не найдено): <span className="font-medium text-fg1">{pending.inline}</span>
            </li>
          </ul>
          {pending.inline > 0 && (
            <p className="text-xs text-fg4">
              Несопоставленные ячейки-ссылки сохранены как обычные данные — их можно дозаполнить/связать вручную после вставки.
            </p>
          )}
        </div>
      </Modal>
    );
  }

  // step === 'map' — строки = ПОЛЯ таблицы, справа выбираем колонку Excel-источник.
  return (
    <Modal open={open} onOpenChange={onOpenChange} title="Сопоставление столбцов" wide
      footer={
        <div className="flex justify-between">
          <Button variant="text" onClick={() => setStep('input')}>← Назад</Button>
          <div className="flex gap-3">
            <Button variant="text" onClick={() => onOpenChange(false)}>Отмена</Button>
            <Button variant="filled" onClick={stage} loading={resolving} disabled={importCount === 0 || resolving}>
              Импортировать {importCount > 0 ? `(${importCount} стр.)` : ''}
            </Button>
          </div>
        </div>
      }>
      <div className="space-y-4">
        <div className="flex items-center gap-3">
          <label className="flex items-center gap-2 text-sm text-fg2 cursor-pointer">
            <input type="checkbox" checked={skipHeader}
              onChange={e => {
                const checked = e.target.checked;
                setSkipHeader(checked);
                setFieldCol(checked ? mapByHeader(allRows[0] ?? [], maxCols) : {});
                setMatchFields({});
                clearPending();
              }}
              className="w-4 h-4 rounded border-stroke-strong text-brand" />
            Первая строка — заголовки
          </label>
          <span className="text-xs text-fg4">Найдено строк данных: {importCount}</span>
        </div>
        <div className="overflow-x-auto">
          <table className="w-full text-xs border-collapse">
            <thead>
              <tr className="bg-base">
                <th className="border border-stroke px-2 py-1.5 text-left text-fg3 font-medium w-1/3">Поле таблицы</th>
                <th className="border border-stroke px-2 py-1.5 text-left text-fg3 font-medium">Столбец из Excel</th>
                <th className="border border-stroke px-2 py-1.5 text-left text-fg3 font-medium">Сопоставить по</th>
              </tr>
            </thead>
            <tbody>
              {tableFields.map(f => {
                const ci = fieldCol[f.key];
                const needsMatch = f.type === 'complex';
                const compositeType = needsMatch
                  ? allDocTypes.find(dt => dt.id === f.typeId) ?? null : null;
                const matchableFields = compositeType
                  ? resolveEffectiveFields(compositeType, allDocTypes).filter(
                    mf => mf.type === 'string' || mf.type === 'number',
                  ) : [];
                return (
                  <tr key={f.key} className="hover:bg-base">
                    <td className="border border-stroke px-2 py-1.5">
                      <span className="text-fg1 font-medium">{f.title}</span>
                      {f.required && <span className="text-danger ml-0.5">*</span>}
                    </td>
                    <td className="border border-stroke px-2 py-1.5">
                      <select value={ci ?? ''}
                        onChange={e => {
                          const v = e.target.value;
                          clearPending();
                          setFieldCol(prev => {
                            const n = { ...prev };
                            if (v === '') delete n[f.key]; else n[f.key] = Number(v);
                            return n;
                          });
                        }}
                        className={selectCls}>
                        <option value="">— пропустить —</option>
                        {Array.from({ length: maxCols }, (_, i) => (
                          <option key={i} value={i}>{colLabel(i)}</option>
                        ))}
                      </select>
                    </td>
                    <td className="border border-stroke px-2 py-1.5">
                      {needsMatch && matchableFields.length > 0 && ci != null && (
                        <select value={matchFields[f.key] ?? ''}
                          onChange={e => { clearPending(); setMatchFields(prev => ({ ...prev, [f.key]: e.target.value })); }}
                          className={selectCls}>
                          <option value="">— выберите поле —</option>
                          {matchableFields.map(mf => <option key={mf.key} value={mf.key}>{mf.title}</option>)}
                        </select>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </div>
    </Modal>
  );
}
