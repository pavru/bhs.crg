import { useState } from 'react';
import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import { Download } from 'lucide-react';
import { exportTable, type TableColumn, type TableExportBody } from '@/shared/api/tables';
import { formatCount } from '@/shared/format/format';
import { useToast } from '@/shared/ui/Toast';
import { ruPlural } from '@/shared/utils/pluralize';
import { TableDataSetDialog } from './TableDataSetDialog';

const itemCls =
  'flex items-center gap-2 px-3 py-1.5 text-sm text-fg2 cursor-pointer select-none outline-none ' +
  'data-[highlighted]:bg-base data-[disabled]:opacity-40 data-[disabled]:pointer-events-none';

/**
 * Кнопка-меню «Выгрузить» (ТЗ CORE-33; задача G3b, issue #1096): «В XLSX» и «Сделать источником
 * данных…». Каждый пункт — только когда он выполним: невыполнимое скрывают (решение владельца
 * 11.10.2026), и меню без пунктов не рисуется вовсе — это решает экран.
 *
 * Число строк названо в самом пункте: в файл идёт ВЕСЬ отбор, а не страница, и человек должен знать
 * это до нажатия — «200 строк на экране» иначе читались бы как «200 строк в файле».
 */
export function ExportMenu({ address, count, body, xlsx: canXlsx, dataset }: {
  address: string;
  /** Сколько строк под отбором всего — не на странице. */
  count: number;
  body: TableExportBody;
  /** Отбор не больше потолка выгрузки — пункт «В XLSX» есть. */
  xlsx: boolean;
  /** Мост в наборы доступен: название по умолчанию и колонки экрана. Не назван — пункта нет. */
  dataset?: { name: string; columns: TableColumn[] };
}) {
  const toast = useToast();
  const [busy, setBusy] = useState(false);
  const [bridging, setBridging] = useState(false);

  const xlsx = async () => {
    setBusy(true);
    try {
      await exportTable(address, body);
    } catch (e) {
      // Отказ сервера словами: «под отбором 61 000 строк… сузьте отбор».
      toast.apiError(e, 'Таблица не выгружена');
    } finally {
      setBusy(false);
    }
  };

  return (
    <DropdownMenu.Root>
      <DropdownMenu.Trigger asChild>
        <button type="button" disabled={busy}
          className="inline-flex items-center gap-1.5 rounded-md border border-stroke px-2.5 py-1 text-xs text-fg2 hover:text-fg1 hover:bg-muted disabled:opacity-60">
          <Download size={13} aria-hidden />
          {busy ? 'Выгружается…' : 'Выгрузить'}
        </button>
      </DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content align="end" sideOffset={6}
          className="z-50 min-w-[14rem] rounded-lg bg-surface border border-stroke py-1 focus:outline-none"
          style={{ boxShadow: 'var(--f-shadow16)' }}>
          {canXlsx && (
            <DropdownMenu.Item className={itemCls} disabled={busy} onSelect={() => void xlsx()}>
              В XLSX · {formatCount(count)} {ruPlural(count, 'строка', 'строки', 'строк')}
            </DropdownMenu.Item>
          )}
          {dataset && (
            <DropdownMenu.Item className={itemCls} onSelect={() => setBridging(true)}>
              Сделать источником данных…
            </DropdownMenu.Item>
          )}
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
      {bridging && dataset && (
        <TableDataSetDialog address={address} initialName={dataset.name} body={body} columns={dataset.columns}
          onClose={() => setBridging(false)} />
      )}
    </DropdownMenu.Root>
  );
}
