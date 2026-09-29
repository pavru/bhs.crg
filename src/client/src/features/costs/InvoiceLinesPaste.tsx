import * as Dialog from '@radix-ui/react-dialog';
import { useState } from 'react';
import { ClipboardPaste } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import {
  PASTE_ROLES, fromTable, guessRoles, hasHeader, parseTable,
  type LineDraft, type PasteRole,
} from './invoiceLines';

/**
 * Вставка строк из буфера (задача C2, issue #1078, ТЗ COST-6.2: «таблица из PDF или Excel»).
 *
 * <p><b>Через поле, а не перехватом события вставки в таблицу.</b> Так вставка работает и там, где
 * браузер не даёт читать буфер без разрешения, и там, где таблицу принесли текстом из письма. Заодно
 * человек видит, ЧТО он вставил, до того как это станет двадцатью строками счёта.</p>
 *
 * <p>⚠️ <b>Роли колонок — догадка, и она названа догадкой.</b> Угадать наверняка нельзя: у одного
 * поставщика «сумма» третья, у другого шестая. Ошибка в роли дороже, чем кажется, — цена, попавшая в
 * количество, даёт правдоподобную сумму, и заметить это можно только по расхождению с бумагой. Поэтому
 * разбор показывается ДО вставки, и роль каждой колонки человек вправе поменять.</p>
 *
 * <p>⚠️ Позиции номенклатуры у вставленных строк нет и быть не может: в бумаге стоят слова поставщика.
 * Сопоставляет их человек (или таблица соответствий, C3), а счёт до тех пор живёт в отборе
 * «Разобрать».</p>
 */
export function InvoiceLinesPaste({ onAdd }: { onAdd: (drafts: LineDraft[]) => void }) {
  const [open, setOpen] = useState(false);

  return (
    <>
      <Button size="sm" variant="outlined" icon={<ClipboardPaste size={13} />}
        onClick={() => setOpen(true)}>
        Вставить из буфера
      </Button>
      {open && <PasteDialog onClose={() => setOpen(false)}
        onAdd={drafts => { onAdd(drafts); setOpen(false); }} />}
    </>
  );
}

function PasteDialog({ onClose, onAdd }: { onClose: () => void; onAdd: (drafts: LineDraft[]) => void }) {
  const [text, setText] = useState('');
  const [roles, setRoles] = useState<PasteRole[] | null>(null);

  const table = parseTable(text);
  const width = Math.max(0, ...table.map(row => row.length));
  // Роли, названные человеком, важнее догадки — но только пока ширина не изменилась: вставил другую
  // таблицу — прежние роли к ней не относятся.
  const current = roles && roles.length === width ? roles : guessRoles(table);
  const rows = hasHeader(table) ? table.slice(1) : table;

  return (
    <Dialog.Root open onOpenChange={o => { if (!o) onClose(); }}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-black/40 z-50" />
        <Dialog.Content className="fixed z-50 left-1/2 top-12 -translate-x-1/2 w-[min(64rem,94vw)]
          rounded-xl border border-stroke bg-surface shadow-xl p-4 space-y-3">
          <Dialog.Title className="text-sm font-medium text-fg2">Вставка строк из буфера</Dialog.Title>

          <textarea value={text} onChange={e => setText(e.target.value)} rows={5}
            aria-label="Таблица из буфера"
            placeholder="Вставьте сюда таблицу товаров — из Excel, из PDF или из письма"
            className="w-full rounded-lg border border-stroke bg-surface2 px-3 py-2 text-xs font-mono
              text-fg outline-none focus:border-primary" />

          {table.length > 0 && (
            <>
              <p className="text-xs text-fg4">
                Разобрано строк: <b className="text-fg2">{rows.length}</b>
                {hasHeader(table) && ' (первая строка принята за шапку и в счёт не пойдёт)'}. Роли
                колонок ниже — <b>догадка</b>: проверьте их, прежде чем добавлять.
              </p>

              <div className="overflow-x-auto max-h-64">
                <table className="text-xs border-collapse">
                  <thead>
                    <tr>
                      {Array.from({ length: width }, (_, column) => (
                        <th key={column} className="p-1 align-top">
                          <select value={current[column]}
                            aria-label={`Колонка ${column + 1}`}
                            onChange={e => setRoles(next(current, column, e.target.value as PasteRole))}
                            className="rounded border border-stroke bg-surface2 px-1 py-0.5 text-[11px]
                              text-fg2">
                            {PASTE_ROLES.map(role => (
                              <option key={role.role} value={role.role}>{role.title}</option>
                            ))}
                          </select>
                        </th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {rows.slice(0, 8).map((row, index) => (
                      <tr key={index} className="border-t border-stroke">
                        {Array.from({ length: width }, (_, column) => (
                          <td key={column}
                            className={`px-1.5 py-1 max-w-56 truncate
                              ${current[column] === 'skip' ? 'text-fg4 line-through' : 'text-fg'}`}>
                            {row[column] ?? ''}
                          </td>
                        ))}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>

              {rows.length > 8 && (
                <p className="text-xs text-fg4">Показаны первые 8 строк из {rows.length} — добавятся все.</p>
              )}
            </>
          )}

          <div className="flex items-center justify-end gap-2">
            <Button size="sm" variant="text" onClick={onClose}>Отмена</Button>
            <Button size="sm" variant="filled" disabled={rows.length === 0}
              onClick={() => onAdd(fromTable(table, current))}>
              Добавить строк: {rows.length}
            </Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}

function next(roles: readonly PasteRole[], column: number, role: PasteRole): PasteRole[] {
  return roles.map((current, index) => (index === column ? role : current));
}
