import { Archive } from 'lucide-react';
import type { ObjectResolveResult } from '@/shared/api/objects';

/** Ячейка вставки, чьё значение совпало с записью в архиве: куда положить ссылку, если запись вернут. */
export interface ArchivedCell {
  row: Record<string, unknown>;
  fieldKey: string;
  hit: ObjectResolveResult;
}

const NAMES_SHOWN = 8;

/**
 * Третье состояние сводки вставки — «в архиве» (issue #1185): значение совпало с записью каталога,
 * но она в архиве. Молча подставить её нельзя — это новая ссылка на запись, убранную из выбора; молча
 * вернуть из архива тоже — возврат меняет справочник для всех. Поэтому ячейки ложатся встроенно, а
 * связать их можно одним осознанным переключателем на всю вставку.
 *
 * Без права вести общие данные переключателя нет вовсе, а текст говорит, что делать дальше:
 * отключённый флажок без объяснения — кнопка в никуда.
 */
export function PasteArchivedNote({ cells, canReturn, checked, onChange }: {
  cells: ArchivedCell[]; canReturn: boolean; checked: boolean; onChange: (v: boolean) => void;
}) {
  const names = [...new Map(cells.map(c => [c.hit.entryId, c.hit.displayName ?? '(без названия)'])).values()];
  return (
    <div className="space-y-1.5 border-t border-muted pt-3">
      <p className="text-fg2 flex items-center gap-1.5">
        <Archive size={13} className="text-fg3 shrink-0" />
        В архиве: <span className="font-medium text-fg1">{cells.length}</span>
      </p>
      <p className="text-xs text-fg3">
        {names.slice(0, NAMES_SHOWN).join(', ')}
        {names.length > NAMES_SHOWN && ` и ещё ${names.length - NAMES_SHOWN}`}
      </p>
      {canReturn && (
        <label className="flex items-center gap-2 text-sm text-fg1 cursor-pointer">
          <input type="checkbox" checked={checked} onChange={e => onChange(e.target.checked)} />
          Вернуть из архива и связать ({names.length})
        </label>
      )}
      <p className="text-xs text-fg4">
        {checked
          ? 'Записи вернутся в справочник для всех — в списках выбора они появятся снова.'
          : `Значения совпали с записями в архиве — вставятся встроенно. ${canReturn
            ? 'Связать их можно и позже, вернув запись из архива.'
            : 'Вернуть запись из архива может тот, кто ведёт общие данные; после этого их можно связать вручную.'}`}
      </p>
    </div>
  );
}
