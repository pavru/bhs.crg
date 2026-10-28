import { Fragment, useState } from 'react';
import { ChevronDown, ChevronRight } from 'lucide-react';
import type { UnplacedRead } from '@/shared/api/invoiceRecognition';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { toggleInSet } from '@/shared/utils/toggleInSet';
import {
  SUMMARY_KEYS, inRows, unplacedGroups, unplacedReason, unplacedSummary, unplacedValues, unplacedWhere,
  type UnplacedGroup,
} from './unplaced';

/**
 * «Ещё прочитано, в счёт не положено» — тихая строка в конце замечаний распознавания и сводка по ключам
 * под ней (ТЗ COST-8, issue #1484, макет 1b).
 *
 * <p><b>Строка тихая.</b> Колонка «только для наборов данных» законна, и предупреждение в каждом счёте
 * стало бы шумом. Свёрнутая строка уже называет ключи строк — опечатку «Артикл» видно, не раскрывая.</p>
 *
 * <p>`null` (распознавание до списка) и `[]` — строки нет: «не знаем» не показывается нулём.</p>
 */
export function UnplacedReads({ list }: { list: readonly UnplacedRead[] | null }) {
  const [shown, setShown] = useState(false);
  // Раскрытые до строк файла ключи — по id строки сводки.
  const [open, setOpen] = useState<ReadonlySet<string>>(new Set());
  if (!list || list.length === 0) return null;

  const { lineKeys, header } = unplacedSummary(list);
  const named = lineKeys.slice(0, SUMMARY_KEYS);
  const more = lineKeys.length - named.length;

  const columns: DataGridColumn<Line>[] = [
    { key: 'key', label: 'Ключ', rowHeader: true,
      cell: ({ group, read }) => {
        // Строка файла под ключом названа им же для читалки экрана: иначе заголовок строки пуст.
        if (read) return <span className="sr-only">{group.key}</span>;
        const many = expandable(group);
        // Ключ — как прочитан: моноширинным и без обрезки пробелов, опечатка «Цена » видна. Не переносится:
        // ключ, разорванный посередине, читается как два; на узком экране таблица прокручивается вбок.
        const key = <span className="font-mono whitespace-pre">{group.key}</span>;
        // Отступ — на ширину шеврона с зазором: ключи стоят одной колонкой.
        if (!many) return <span className="block pl-3.5">{key}</span>;
        return (
          <button type="button" aria-expanded={open.has(group.id)} onClick={() => setOpen(was => toggleInSet(was, group.id))}
            className="inline-flex items-center gap-0.5 text-left">
            {open.has(group.id) ? <ChevronDown size={12} aria-hidden /> : <ChevronRight size={12} aria-hidden />}
            {key}
          </button>
        );
      } },
    // «Где» не переносится: «строки 1–12», разорванное на дефисе, читается как две строки.
    { key: 'where', label: 'Где',
      cell: ({ group, read }) => <span className="text-fg3">{read ? `строка ${read.row}` : unplacedWhere(group)}</span> },
    { key: 'value', label: 'Прочитано', wrap: true,
      cell: ({ group, read }) => read ? read.value : unplacedValues(group) },
    { key: 'reason', label: 'Причина', wrap: true,
      cell: ({ group, read }) => read ? null : unplacedReason(group) },
  ];

  return (
    <div className="space-y-1 text-fg3" data-testid="unplaced">
      <p>
        Ещё прочитано, в счёт не положено:{' '}
        {named.map((line, i) => (
          <Fragment key={line.key}>
            {i > 0 && ', '}
            <span className="font-mono font-medium whitespace-pre text-on-surface-variant">{line.key}</span> ({inRows(line.rows)})
          </Fragment>
        ))}
        {more > 0 && ` и ещё ключей: ${more}`}
        {header > 0 && <>{lineKeys.length > 0 ? ', и в шапке: ' : 'в шапке: '}<span className="font-medium text-on-surface-variant">{header}</span></>}
        {' '}
        <button type="button" aria-expanded={shown} onClick={() => setShown(s => !s)}
          className="inline-flex items-center gap-0.5 text-primary hover:underline">
          {shown ? 'Скрыть' : 'Показать'}
          <ChevronDown size={12} aria-hidden className={shown ? 'rotate-180' : undefined} />
        </button>
      </p>
      {shown && (
        <DataGrid density="compact" className="max-h-96" columns={columns} rowKey={line => line.id}
          rows={lines(unplacedGroups(list), open)} />
      )}
    </div>
  );
}

/**
 * Строка сетки: ключ сводки или одна строка файла под раскрытым ключом. Строки файла — своими строками,
 * а не текстом в клетке: так их значения стоят в колонке «Прочитано», под значениями ключа.
 */
interface Line {
  id: string;
  group: UnplacedGroup;
  read: UnplacedRead | null;
}

/** Раскрывается только ключ строк файла, прочитанный не раз: у записей шапки номеров строк нет. */
const expandable = (group: UnplacedGroup) => group.place === 'line' && group.items.length > 1;

function lines(groups: UnplacedGroup[], open: ReadonlySet<string>): Line[] {
  return groups.flatMap(group => {
    const head: Line = { id: group.id, group, read: null };
    return open.has(group.id) && expandable(group)
      ? [head, ...group.items.map((read, i) => ({ id: `${group.id}#${i}`, group, read }))]
      : [head];
  });
}
