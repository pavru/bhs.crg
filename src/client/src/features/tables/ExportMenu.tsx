import { useState } from 'react';
import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import { Download } from '@/shared/ui/icons';
import { exportTable, type TableColumn, type TableExportBody } from '@/shared/api/tables';
import { formatCount } from '@/shared/format/format';
import { useToast } from '@/shared/ui/Toast';
import { ruPlural } from '@/shared/utils/pluralize';
import { TableDataSetDialog } from './TableDataSetDialog';
import { Chip } from '@/shared/ui/Chip';

const itemCls =
  'flex items-center gap-2 px-3 py-1.5 text-body-medium text-on-surface-variant cursor-pointer select-none outline-none ' +
  'state-layer disabled-content data-[disabled]:pointer-events-none';

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
  dataset?: { name: string; columns: TableColumn[]; all: TableColumn[] };
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
        {/* В ряду чипов отбора — `Chip menu` ([UI-42], #1416), как «Колонки» рядом. */}
        <Chip menu disabled={busy} icon={Download}>{busy ? 'Выгружается…' : 'Выгрузить'}</Chip>
      </DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content align="end" sideOffset={6}
          className="z-50 min-w-[14rem] rounded-md bg-surface border border-outline-variant py-1 focus:outline-none shadow-elevation-2">
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
          all={dataset.all}
          onClose={() => setBridging(false)} />
      )}
    </DropdownMenu.Root>
  );
}
