import { useMemo, useState } from 'react';
import { costHidden, type HiddenPart } from '@/shared/api/planningEstimates';
import { useConjunctureItems, type ConjunctureItem, type ConjunctureOffer } from '@/shared/api/planningConjuncture';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { SearchInput } from '@/shared/ui/SearchInput';
import { formatAmount, formatDay } from '@/shared/format/format';
import { apiError } from '@/shared/utils/apiError';
import { kaLines, noPricesWords, offerWords, selectionWords, supplierWords, type KaLine } from './attachmentRows';

/**
 * Конъюнктурный анализ, раскрытый под списком приложений (change `plan-11-conjuncture`, макет П11, вариант 1):
 * главы — заголовками групп, у позиции предложения — строками под ней, выбранное и минимальное помечены словом;
 * одно предложение — одна строка, без цен у всех — одной строкой с поставщиками.
 *
 * Без `plan.estimate.cost` колонок цены нет вовсе: на их месте одна колонка «Цена» в состоянии «нет права» с именем
 * права из каталога (CORE-33), над сеткой — строка о скрытом (D4 зонтика). Поставщики, ИНН, даты и способ выбора —
 * видны (вопрос 6 листа 0, предложенный ответ).
 */
export function ConjunctureView({ analysisId }: { analysisId: string }) {
  const items = useConjunctureItems(analysisId);
  const [query, setQuery] = useState('');
  const lines = useMemo(() => kaLines(items.data?.items ?? [], query), [items.data, query]);

  if (items.isError) return <p role="alert" className="text-body-medium text-error">{apiError(items.error, 'Позиции анализа не прочитались')}</p>;
  if (!items.data) return <p className="text-body-medium text-on-surface-variant">Читаем позиции анализа…</p>;
  const closed = costHidden(items.data.hidden);

  return (
    <div className="space-y-3">
      <SearchInput value={query} onChange={setQuery} placeholder="Найти позицию" ariaLabel="Поиск позиции по номеру, наименованию или поставщику"
        className="w-64 max-w-full" />
      {closed && (
        <p className="text-body-small text-on-surface-variant">
          Цены предложений скрыты: их показывает право «{closed.name}» — его выдаёт администратор.
        </p>
      )}
      <DataGrid<KaLine> columns={kaColumns(items.data.hidden)} rows={lines} rowKey={l => l.key} framed={false} density="compact" fixed
        state={query.trim() && lines.length === 0 ? 'filtered-out' : undefined}
        section={l => (l.kind === 'chapter' ? <span className="text-title-small">{l.caption}</span> : null)} />
    </div>
  );
}

const selectedOf = (item: ConjunctureItem) => item.offers.find(o => o.selected) ?? null;
const money = (v: number | null | undefined) => (v == null ? null : <span className="tabular-nums">{formatAmount(v)}</span>);
const muted = (item: ConjunctureItem) => (item.inactive ? 'text-on-surface-variant' : '');

/** Предложение, чья цена стоит в строке позиции: у одного предложения — оно, иначе выбранное. */
const shownOffer = (l: KaLine): ConjunctureOffer | null =>
  l.kind === 'offer' ? l.offer : l.kind === 'item' ? (l.single ? l.item.offers[0] ?? null : selectedOf(l.item)) : null;

/** Колонки сетки КА. Цены — только с правом: без него одна колонка «Цена» называет причину, а не пустует. */
function kaColumns(hidden: HiddenPart[] | undefined): DataGridColumn<KaLine>[] {
  const closed = costHidden(hidden);
  const prices: DataGridColumn<KaLine>[] = closed
    ? [{
        key: 'price', label: 'Цена', align: 'right', width: 'w-32', unavailable: 'no-right',
        unavailableHint: `Цены видны с правом «${closed.name}» — его выдаёт администратор.`,
      }]
    : [
        {
          key: 'price', label: 'Без НДС, ₽', align: 'right', width: 'w-28',
          cell: l => (l.kind === 'item' && !l.single ? money(l.item.cost?.selectedPrice) : money(shownOffer(l)?.cost?.price)),
        },
        { key: 'priceWithVat', label: 'С НДС, ₽', align: 'right', width: 'w-28', cell: l => money(shownOffer(l)?.cost?.priceWithVat) },
      ];
  return [
    {
      key: 'number', label: '№', width: 'w-12',
      cell: l => (l.kind === 'item' ? <span className="tabular-nums text-on-surface-variant">{l.item.number}</span> : null),
    },
    { key: 'caption', label: 'Позиция · предложение', wrap: true, rowHeader: true, cell: l => <CaptionCell line={l} /> },
    { key: 'units', label: 'Ед.', width: 'w-14', cell: l => (l.kind === 'item' ? l.item.units : null) },
    ...prices,
    { key: 'selection', label: 'Выбор · дата', width: 'w-44', wrap: true, cell: l => <SelectionCell line={l} /> },
  ];
}

function CaptionCell({ line }: { line: KaLine }) {
  switch (line.kind) {
    case 'item': {
      const only = line.single ? line.item.offers[0] : undefined;
      return (
        <span className={`flex flex-col ${muted(line.item)}`}>
          <span className="text-body-medium">{line.item.caption}</span>
          {only && <span className="text-body-small text-on-surface-variant">{supplierWords(only)}</span>}
        </span>
      );
    }
    case 'offer':
      return <span className="block pl-4 text-body-small">{supplierWords(line.offer)}</span>;
    case 'no-prices':
      return <span className="block pl-4 text-body-small text-on-surface-variant">{noPricesWords(line.item)}</span>;
    default:
      return null;
  }
}

/** Способ выбора у позиции, пометка «выбрано» / «минимальная» и дата цены у предложения. */
function SelectionCell({ line }: { line: KaLine }) {
  if (line.kind === 'item') {
    const date = line.single ? line.item.offers[0]?.date : null;
    return <span className={`text-body-small ${muted(line.item)}`}>{[selectionWords(line.item), date ? formatDay(date) : null].filter(Boolean).join(' · ')}</span>;
  }
  if (line.kind !== 'offer') return null;
  const words = offerWords(line.item, line.offer);
  const date = line.offer.date ? formatDay(line.offer.date) : null;
  return (
    <span className="text-body-small">
      {words && <span className="text-primary">{words}</span>}
      {words && date ? ' · ' : null}
      {date && <span className="text-on-surface-variant">{date}</span>}
    </span>
  );
}
