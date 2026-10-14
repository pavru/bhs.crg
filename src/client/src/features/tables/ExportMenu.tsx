import { useState } from 'react';
import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import { Download } from 'lucide-react';
import { exportTable, type TableExportBody } from '@/shared/api/tables';
import { formatCount } from '@/shared/format/format';
import { useToast } from '@/shared/ui/Toast';
import { ruPlural } from '@/shared/utils/pluralize';

const itemCls =
  'flex items-center gap-2 px-3 py-1.5 text-sm text-fg2 cursor-pointer select-none outline-none ' +
  'data-[highlighted]:bg-base data-[disabled]:opacity-40 data-[disabled]:pointer-events-none';

/**
 * Кнопка-меню «Выгрузить» (ТЗ CORE-33; задача G3b, issue #1096). Меню, а не кнопка, потому что
 * пунктов будет два: «В XLSX» и «Сделать источником данных…» — второй приходит с мостом в наборы.
 *
 * Число строк названо в самом пункте: в файл идёт ВЕСЬ отбор, а не страница, и человек должен знать
 * это до нажатия — «200 строк на экране» иначе читались бы как «200 строк в файле».
 */
export function ExportMenu({ address, count, body }: {
  address: string;
  /** Сколько строк под отбором всего — не на странице. */
  count: number;
  body: TableExportBody;
}) {
  const toast = useToast();
  const [busy, setBusy] = useState(false);

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
          <DropdownMenu.Item className={itemCls} disabled={busy} onSelect={() => void xlsx()}>
            В XLSX · {formatCount(count)} {ruPlural(count, 'строка', 'строки', 'строк')}
          </DropdownMenu.Item>
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  );
}
