import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { ContentPaste } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';
import { Checkbox } from '@/shared/ui/Checkbox';
import { Modal, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import type { VatMode } from '@/shared/api/invoices';
import {
  PASTE_ROLES, fromTable, guessRoles, hasHeader, parseTable, pastedWithoutVat,
  type LineDraft, type PasteRole,
} from './invoiceLines';
import { Select, SelectItem } from '@/shared/ui/Select';
import { DataGrid } from '@/shared/ui/DataGrid';

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
 *
 * <p><b>Режим НДС — в том же окне</b> (issue #1499): шапка «Цена без НДС» ставит отметку «Цены без НДС, НДС
 * сверху», и от неё же зависит, какая из двух сумм («без НДС | с НДС») станет суммой строки. Вставка без
 * этого заводила счёт в расхождение ровно на НДС — ту самую ошибку, с которой начат эпик #1496.</p>
 */
export function InvoiceLinesPaste({ mode, onAdd }: {
  /** Режим черновика строк — с него начинается отметка в окне. */
  mode: VatMode;
  onAdd: (drafts: LineDraft[], mode: VatMode) => void;
}) {
  const [open, setOpen] = useState(false);

  return (
    <>
      <Button variant="outlined" icon={<Icon icon={ContentPaste} size={18} />}
        onClick={() => setOpen(true)}>
        Вставить из буфера
      </Button>
      {open && <PasteDialog mode={mode} onClose={() => setOpen(false)}
        onAdd={(drafts, chosen) => { onAdd(drafts, chosen); setOpen(false); }} />}
    </>
  );
}

function PasteDialog({ mode, onClose, onAdd }: {
  mode: VatMode; onClose: () => void; onAdd: (drafts: LineDraft[], mode: VatMode) => void;
}) {
  const [text, setText] = useState('');
  const [roles, setRoles] = useState<PasteRole[] | null>(null);
  // Отметка, поставленная или снятая человеком; `null` — догадка по шапке.
  const [onTop, setOnTop] = useState<boolean | null>(null);

  const table = parseTable(text);
  const width = Math.max(0, ...table.map(row => row.length));
  // Отметка в окне только СТАВИТСЯ (ревью PR #1508): у счёта сверху бумага та же, и снять её вставкой
  // значило бы переодеть уже лежащие строки, хотя человек вставлял новые. Снимают её над строками.
  const offered = mode === 'Included';
  const byHeader = offered && pastedWithoutVat(table);
  const chosen: VatMode = offered ? ((onTop ?? byHeader) ? 'OnTop' : 'Included') : mode;
  // Роли, названные человеком, важнее догадки — но только пока ширина не изменилась: вставил другую
  // таблицу — прежние роли к ней не относятся.
  const current = roles && roles.length === width ? roles : guessRoles(table, chosen);
  const rows = hasHeader(table) ? table.slice(1) : table;

  return (
    // Общий `Modal`, а не свой Radix (issue #1236): вставленную таблицу и расставленные роли жалко
    // так же, как любую форму, — а голый диалог закрывался промахом и Esc. Роли выбирают списком,
    // вставка идёт из буфера — признак правок называем сами. Он — про таблицу: роли без неё ни к
    // чему не относятся (и отбрасываются, стоит ширине смениться).
    <Modal open wide onOpenChange={o => { if (!o) onClose(); }} title="Вставка строк из буфера"
      isDirty={text.trim() !== ''}
      footer={
        <>
          <ModalExit />
          <ModalPrimary disabled={rows.length === 0}
            onClick={() => onAdd(fromTable(table, current), chosen)}>
            Добавить строк: {rows.length}
          </ModalPrimary>
        </>}>
        <div className="space-y-3">

          <textarea value={text} onChange={e => setText(e.target.value)} rows={5}
            aria-label="Таблица из буфера"
            placeholder="Вставьте сюда таблицу товаров — из Excel, из PDF или из письма"
            className="w-full rounded-md border border-outline-variant bg-surface-container px-3 py-2 text-body-small font-mono
              text-on-surface outline-none focus:border-primary" />

          {table.length > 0 && (
            <>
              <p className="text-body-small text-on-surface-variant">
                Разобрано строк: <b className="text-on-surface-variant">{rows.length}</b>
                {hasHeader(table) && ' (первая строка принята за шапку и в счёт не пойдёт)'}. Роли
                колонок ниже — <b>догадка</b>: проверьте их, прежде чем добавлять.
              </p>

              {/* Сетка чтения, а не правки: строки здесь не правят — только смотрят, что разобрано, и
                  называют роль колонки выбором в шапке. */}
              <DataGrid<PastedRow> density="compact" className="max-h-64"
                rows={rows.slice(0, 8).map((cells, index) => ({ index, cells }))} rowKey={row => String(row.index)}
                columns={Array.from({ length: width }, (_, column) => ({
                  key: `c${column}`, label: `Колонка ${column + 1}`,
                  header: (
                    <Select density="dense" className="w-auto" value={current[column]}
                      aria-label={`Колонка ${column + 1}`}
                      onValueChange={role => setRoles(next(current, column, role as PasteRole))}>
                      {PASTE_ROLES.map(role => (
                        <SelectItem key={role.role} value={role.role}>{role.title}</SelectItem>
                      ))}
                    </Select>
                  ),
                  cell: row => (
                    <span className={`block max-w-56 truncate ${current[column] === 'skip' ? 'text-on-surface-variant line-through' : ''}`}>
                      {row.cells[column] ?? ''}
                    </span>
                  ),
                }))} />

              {rows.length > 8 && (
                <p className="text-body-small text-on-surface-variant">Показаны первые 8 строк из {rows.length} — добавятся все.</p>
              )}

              {offered ? (
                <Checkbox label="Цены без НДС, НДС сверху" checked={chosen === 'OnTop'}
                  onChange={on => setOnTop(on)}
                  description={chosen === 'Included' ? undefined
                    : `${byHeader && onTop === null ? 'В шапке таблицы цены без НДС. ' : ''}Отметка встанет и над строками счёта — для всех его строк.`} />
              ) : mode === 'OnTop' && (
                <p className="text-body-small text-on-surface-variant">Цены без НДС, НДС сверху — как отмечено над строками счёта.</p>
              )}
            </>
          )}

        </div>
    </Modal>
  );
}

/** Строка разобранной таблицы — с номером: другого ключа у неё нет. */
interface PastedRow { index: number; cells: string[] }

function next(roles: readonly PasteRole[], column: number, role: PasteRole): PasteRole[] {
  return roles.map((current, index) => (index === column ? role : current));
}
