import type { ReactNode } from 'react';
import type { EditGridColumn } from '@/shared/ui/EditGrid';
import { formatInputAmount } from '@/shared/format/format';
import type { InvoiceView } from '@/shared/api/invoices';
import { LOST } from './lostReferences';
import { K, refEntryId } from './invoiceFields';
import { preview, type LineDraft } from './invoiceLines';
import { applyOffer, cancelMatch, lineKey, memoryFate, pickByHand, sameKey, type MatchOffer } from './supplierMatches';
import { LineMatchChip } from './LineMatchChip';
import { NomenclaturePicker } from './NomenclaturePicker';
import { LineAllocationCell } from './LineAllocation';
import { Cell, LockedPosition } from './InvoiceLineCells';

/**
 * Колонки строк счёта для `EditGrid` (#1354, 2/3). Вынесены из `InvoiceLinesTable`: там живёт черновик
 * и его сохранение, здесь — что стоит в каждой клетке, у правимой строки и у запертой.
 *
 * <p>⚠️ <b>Запертая строка — текстом, без полей</b> (закрытый период или нет права вводить счета):
 * поле, в которое можно печатать, обещало бы правку, которой не будет. Разноска открывается и у
 * неё — посмотреть.</p>
 */

/** Пометки ссылки относятся к ПРЕЖНЕЙ позиции: выбор и снятие их сбрасывают разом. */
const NO_MARKS = { nomenclatureLost: false, nomenclatureMoved: false, nomenclatureArchived: false } as const;

export interface InvoiceLineContext {
  view: InvoiceView;
  /** Строки только читаются: счёт заперт либо права вводить счета нет. */
  still: boolean;
  /** Разноску запирает только закрытый период: без права на счета её по-прежнему решает своё право. */
  allocationLocked: boolean;
  /** Почему разносить сейчас нельзя (строки не сохранены), либо `null`. */
  blocked: string | null;
  /** Запомненное для строки, известное форме (C3, issue #1079). */
  offerFor: (draft: LineDraft) => MatchOffer | null;
  /** У счёта сохранён поставщик: без него ни подставить, ни запомнить. */
  hasSupplier: boolean;
  /** У строки появился новый ключ (наименование или артикул) — спросить запомненное. */
  onBorn: (draft: LineDraft) => void;
  /** Диалог разноски строки открыт или закрыт — пока открыт, таблица не пересобирается. */
  onAllocating: (open: boolean) => void;
  onEdit: (key: string, patch: Partial<LineDraft>) => void;
}

/**
 * Хук, а не простая функция: клетки зовут обработчики формы (`onBorn` спрашивает запомненное и держит
 * спрошенное в `ref`), и колонки собираются тем же рендером, что и форма.
 */
export function useInvoiceLineColumns(ctx: InvoiceLineContext): EditGridColumn<LineDraft>[] {
  const { view, still } = ctx;
  const saved = (draft: LineDraft) => view.lines.find(line => line.id === draft.id);

  /**
   * Правка наименования или артикула меняет КЛЮЧ строки: запомненное для прежнего ключа к ней больше не
   * относится. Подстановка снимается вместе с позицией — и у сохранённой строки тоже: «Кабель»,
   * переписанный в «Доставку», иначе остался бы с позицией кабеля и пометкой «запомнено», а сервер
   * такую пометку отвергает («соответствие эту строку не узнаёт»). Регистр и пробелы ключа не меняют.
   */
  function editKey(draft: LineDraft, patch: Partial<LineDraft>) {
    const next = { ...draft, ...patch };
    const moved = !sameKey(lineKey(draft.supplierCode, draft.supplierText), lineKey(next.supplierCode, next.supplierText));
    if (!moved) { ctx.onEdit(draft.key, patch); return; }
    ctx.onEdit(draft.key, {
      ...patch, offer: null, declined: false,
      ...(draft.matchedBy !== null ? { nomenclatureId: null, nomenclatureName: null, matchedBy: null, match: null } : {}),
    });
  }

  /** Строка «родилась» — новая либо с ключом, которого в сохранённой не было: лежащие ждут кнопки. */
  function keyLeft(draft: LineDraft) {
    const line = saved(draft);
    const was = line ? lineKey(line.supplierCode, line.supplierText) : null;
    if (!line || !sameKey(was, lineKey(draft.supplierCode, draft.supplierText))) ctx.onBorn(draft);
  }

  /** Клетка текста: у правимой строки — поле, у запертой — значение. */
  const text = (key: string, label: string, width: string, field: keyof LineDraft & string, options: {
    numeric?: boolean; keyField?: boolean; computed?: (draft: LineDraft) => number | null;
  } = {}): EditGridColumn<LineDraft> => ({
    key, label, width, align: options.numeric ? 'right' : undefined,
    cell: (draft, index) => {
      const value = draft[field] as string;
      const computed = options.computed?.(draft) ?? null;
      const shown = computed === null ? '' : formatInputAmount(computed);
      if (still) return value || shown || '—';
      const name = `${label}, строка ${index + 1}`;
      const change = (next: string) => (options.keyField
        ? editKey(draft, { [field]: next })
        : ctx.onEdit(draft.key, { [field]: next }));
      return (
        <Cell value={value} label={name} numeric={options.numeric} placeholder={shown} onChange={change}
          onBlur={options.keyField ? () => keyLeft(draft) : undefined} />
      );
    },
  });

  const columns: EditGridColumn<LineDraft>[] = [
    {
      key: 'nomenclature', label: 'Позиция номенклатуры', width: 'w-64',
      cell: draft => (still ? <LockedPosition draft={draft} /> : (
        // Потерю называет СЕРВЕР: у формы на все случаи пустого названия один признак, и выбранная
        // только что позиция без имени краснела бы как потерянная. Выбор и снятие потерю снимают —
        // ссылка меняется здесь же, и прежний ответ сервера к ней уже не относится.
        <NomenclaturePicker chosen={draft.nomenclatureId !== null} name={draft.nomenclatureName}
          lost={draft.nomenclatureLost} lostText={draft.nomenclatureMoved ? LOST.movedPosition : undefined}
          archived={draft.nomenclatureArchived}
          clearable={draft.matchedBy === null}
          from={{ name: draft.supplierText, code: draft.supplierCode, unit: draft.unit }}
          mark={(
            <LineMatchChip draft={draft} offer={ctx.offerFor(draft)}
              fate={memoryFate(draft, saved(draft), ctx.hasSupplier, ctx.offerFor(draft))}
              supplierId={refEntryId(view.requisites[K.supplier])}
              onCancel={() => ctx.onEdit(draft.key, cancelMatch(draft))}
              onRestore={restored => ctx.onEdit(draft.key, applyOffer(restored))}
              onRemember={remember => ctx.onEdit(draft.key, { remember })} />
          )}
          onPick={(id, name) => ctx.onEdit(draft.key, pickByHand(id, name))}
          onClear={() => ctx.onEdit(draft.key, {
            nomenclatureId: null, nomenclatureName: null, matchedBy: null, match: null, ...NO_MARKS,
          })} />
      )),
    },
    text('supplierText', 'Наименование в счёте', 'w-56', 'supplierText', { keyField: true }),
    text('supplierCode', 'Артикул', 'w-24', 'supplierCode', { keyField: true }),
    text('unit', 'Единица', 'w-16', 'unit'),
    text('quantity', 'Количество', 'w-20', 'quantity', { numeric: true }),
    text('price', 'Цена', 'w-24', 'price', { numeric: true }),
    text('vatRate', 'Ставка НДС', 'w-16', 'vatRate', { numeric: true }),
    text('vatAmount', 'Сумма НДС', 'w-24', 'vatAmount', { numeric: true, computed: d => preview(d).vat }),
    text('amount', 'Сумма', 'w-28', 'amount', { numeric: true, computed: d => preview(d).amount }),
    text('note', 'Примечание', 'w-40', 'note'),
    {
      key: 'allocation', label: 'Разноска', width: 'w-32',
      cell: (draft, index) => (
        <LineAllocationCell view={view} line={saved(draft)} number={index + 1}
          blocked={still ? null : ctx.blocked} locked={still ? ctx.allocationLocked : false}
          onOpenChange={ctx.onAllocating} />
      ),
    },
  ];
  return columns.map(withShortHeader);
}

/**
 * Подпись поля для читалки — полная («Количество, строка 2»): по ней же клетку находит живой прогон. В
 * шапке, где колонок двенадцать, — короткая.
 */
const SHORT: Record<string, ReactNode> = {
  unit: 'Ед.', quantity: 'Кол-во', vatRate: <>НДС&nbsp;%</>,
};

function withShortHeader(column: EditGridColumn<LineDraft>): EditGridColumn<LineDraft> {
  return SHORT[column.key] ? { ...column, header: SHORT[column.key] } : column;
}
