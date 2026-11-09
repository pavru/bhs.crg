import type { ReactNode } from 'react';
import type { EditGridColumn } from '@/shared/ui/EditGrid';
import { formatInputAmount } from '@/shared/format/format';
import type { InvoiceView, VatMode } from '@/shared/api/invoices';
import { LOST } from './lostReferences';
import { K, refEntryId } from './invoiceFields';
import { preview, type LineDraft } from './invoiceLines';
import { applyOffer, cancelMatch, lineKey, memoryFate, pickByHand, sameKey, type MatchOffer } from './supplierMatches';
import { LineMatchChip } from './LineMatchChip';
import { NomenclaturePicker } from './NomenclaturePicker';
import { invoiceLineWhere } from './pickerLine';
import { neighboursOf } from './pickerCandidates';
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

/** Позиция строки для соседей в окне выбора; потерянная и архивная — не кандидат. */
const chosenPosition = (d: LineDraft) => (d.nomenclatureId && !d.nomenclatureLost && !d.nomenclatureArchived
  ? { name: d.supplierText, code: d.supplierCode, positionId: d.nomenclatureId, positionName: d.nomenclatureName }
  : null);

/** Пометки ссылки относятся к ПРЕЖНЕЙ позиции: выбор и снятие их сбрасывают разом. */
const NO_MARKS = { nomenclatureLost: false, nomenclatureMoved: false, nomenclatureArchived: false } as const;

export interface InvoiceLineContext {
  view: InvoiceView;
  /** Строки черновика — соседи строки в окне выбора позиции (issue #1518). */
  drafts: readonly LineDraft[];
  /** Строки только читаются: счёт заперт либо права вводить счета нет. */
  still: boolean;
  /** Режим НДС черновика (issue #1499): от него — подписи цены и суммы и досчёт НДС. */
  mode: VatMode;
  /** Разноску запирает только закрытый период: без права на счета её по-прежнему решает своё право. */
  allocationLocked: boolean;
  /** Почему разносить сейчас нельзя (строки не сохранены), либо `null`. */
  blocked: string | null;
  /** Запомненное для строки, известное форме (C3, issue #1079). */
  offerFor: (draft: LineDraft) => MatchOffer | null;
  /** У счёта есть поставщик — сохранённый или, при несохранённой смене, из шапки: без него не запомнить. */
  hasSupplier: boolean;
  /** Поставщик в шапке изменён и не сохранён (#1657): пометкам строк есть что об этом сказать. */
  supplierPending?: boolean;
  /** Сохранённый поставщик названием — в плашку окна выбора позиции. */
  supplierName: string | null;
  /** У строки появился новый ключ (наименование или артикул) — спросить запомненное. */
  onBorn: (draft: LineDraft) => void;
  /** Диалог разноски строки открыт или закрыт — пока открыт, таблица не пересобирается. */
  onAllocating: (open: boolean) => void;
  onEdit: (key: string, patch: Partial<LineDraft>) => void;
  /** Плотный вид — рядом список счетов: «Разноска» уже (D-10.10). Высоты строк те же. */
  compact?: boolean;
  /** Колонка «Примечание» включена (чип над таблицей) — у запертой строки она есть, только если примечания есть. */
  note?: boolean;
  /** Колонки НДС строки — ставка и сумма — включены (чип над таблицей). */
  vat?: boolean;
}

/**
 * Хук, а не простая функция: клетки зовут обработчики формы (`onBorn` спрашивает запомненное и держит
 * спрошенное в `ref`), и колонки собираются тем же рендером, что и форма.
 */
export function useInvoiceLineColumns(ctx: InvoiceLineContext): EditGridColumn<LineDraft>[] {
  const { view, still, mode } = ctx;
  // Сверху цена и сумма строки — без НДС, и подпись обязана это сказать: её же читает читалка («Цена без
  // НДС, строка 2»). Колонки «с НДС» нет нарочно — сумма с НДС видна в сверке и в разноске.
  const net = mode === 'OnTop' ? ' без НДС' : '';
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
      key: 'nomenclature', label: 'Позиция номенклатуры',
      cell: (draft, index) => (still ? <LockedPosition draft={draft} /> : (
        // Потерю называет СЕРВЕР: у формы на все случаи пустого названия один признак, и выбранная
        // только что позиция без имени краснела бы как потерянная. Выбор и снятие потерю снимают —
        // ссылка меняется здесь же, и прежний ответ сервера к ней уже не относится.
        <NomenclaturePicker chosen={draft.nomenclatureId !== null} chosenId={draft.nomenclatureId}
          name={draft.nomenclatureName} supplierId={refEntryId(view.requisites[K.supplier])}
          neighbours={() => neighboursOf(ctx.drafts, index, chosenPosition)}
          lost={draft.nomenclatureLost} lostText={draft.nomenclatureMoved ? LOST.movedPosition : undefined}
          archived={draft.nomenclatureArchived}
          clearable={draft.matchedBy === null}
          from={{ name: draft.supplierText, code: draft.supplierCode, unit: draft.unit }}
          line={{ where: invoiceLineWhere(index + 1, ctx.supplierName), quantity: draft.quantity }}
          mark={(
            <LineMatchChip draft={draft} offer={ctx.offerFor(draft)}
              fate={memoryFate(draft, saved(draft), ctx.hasSupplier, ctx.offerFor(draft))}
              supplierPending={ctx.supplierPending}
              leaving={ctx.supplierPending === true && draft.matchedBy !== null && saved(draft)?.match?.id !== draft.matchedBy}
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
    nameColumn(),
    text('quantity', 'Количество', 'w-16', 'quantity', { numeric: true }),
    text('unit', 'Единица', 'w-13', 'unit'),
    text('price', `Цена${net}`, 'w-22', 'price', { numeric: true }),
    // Ставка строки нужна сверху всегда: без неё пункт готовности о ставке не исправить на месте. В режиме
    // «в том числе» НДС строки называет сверка, а колонки — по чипу «НДС строк» (D-10.10, ревью PR #1786).
    ...(mode === 'OnTop' || ctx.vat ? [text('vatRate', 'Ставка НДС', 'w-16', 'vatRate', { numeric: true })] : []),
    ...(ctx.vat ? [text('vatAmount', 'Сумма НДС', 'w-22', 'vatAmount', { numeric: true, computed: d => preview(d, mode).vat })] : []),
    text('amount', `Сумма${net}`, 'w-25', 'amount', { numeric: true, computed: d => preview(d, mode).amount }),
    // Примечание — колонкой по чипу: таблица обязана помещаться без прокрутки вбок, а примечание пишут
    // редко. Набранное не прячется: у запертой строки колонка есть, когда примечания есть.
    ...((still ? ctx.drafts.some(d => d.note.trim()) : ctx.note) ? [text('note', 'Примечание', 'w-40', 'note')] : []),
    {
      key: 'allocation', label: 'Разноска', width: ctx.compact ? 'w-24' : 'w-29',
      cell: (draft, index) => (
        <LineAllocationCell view={view} line={saved(draft)} number={index + 1}
          blocked={still ? null : ctx.blocked} locked={still ? ctx.allocationLocked : false}
          onOpenChange={ctx.onAllocating} />
      ),
    },
  ];
  /**
   * Наименование в счёте и артикул подписью в той же клетке (D-10.10): обе — ключ строки, и правятся рядом.
   * Отдельная колонка артикула не помещалась бы без прокрутки вбок на 1280.
   */
  function nameColumn(): EditGridColumn<LineDraft> {
    return {
      key: 'supplierText', label: 'Наименование в счёте',
      cell: (draft, index) => {
        if (still)
          return (
            <span>
              {draft.supplierText || '—'}
              {draft.supplierCode && <span className="text-body-small text-on-surface-variant"> · {draft.supplierCode}</span>}
            </span>
          );
        return (
          // Клетка уже 160 px (форма рядом с файлом на 1280) — артикула нет: поле 80 px оставило бы
          // наименованию 20. Его видно и правят на месте шире — с закрытым файлом или окном шире.
          <div className="@container flex items-center gap-1 min-w-0">
            <div className="flex-1 min-w-0">
              <Cell value={draft.supplierText} label={`Наименование в счёте, строка ${index + 1}`}
                onChange={next => editKey(draft, { supplierText: next })} onBlur={() => keyLeft(draft)} />
            </div>
            <div className="hidden @[10rem]:block w-20 shrink-0">
              <Cell value={draft.supplierCode} label={`Артикул, строка ${index + 1}`} placeholder="артикул"
                onChange={next => editKey(draft, { supplierCode: next })} onBlur={() => keyLeft(draft)} />
            </div>
          </div>
        );
      },
    };
  }

  return columns.map(column => withShortHeader(column, mode));
}

/**
 * Подпись поля для читалки — полная («Количество, строка 2»): по ней же клетку находит живой прогон. В
 * шапке, где колонок двенадцать, — короткая.
 */
const SHORT: Record<string, ReactNode> = {
  unit: 'Ед.', quantity: 'Кол-во', vatRate: <>НДС&nbsp;%</>, vatAmount: 'НДС',
};

/** Сверху — в две строки, а не сокращением «б/НДС»: ширину колонок менять не придётся. */
const SHORT_NET: Record<string, ReactNode> = {
  price: <>Цена<br />без НДС</>, amount: <>Сумма<br />без НДС</>,
};

function withShortHeader(column: EditGridColumn<LineDraft>, mode: VatMode): EditGridColumn<LineDraft> {
  const short = (mode === 'OnTop' ? SHORT_NET[column.key] : undefined) ?? SHORT[column.key];
  return short ? { ...column, header: short } : column;
}
