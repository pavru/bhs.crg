import { Archive, ArchiveRestore } from 'lucide-react';
import { useSetCommonDataArchive } from '@/shared/api/commonData';
import { useCan } from '@/shared/api/access';
import type { CommonDataEntry } from '@/shared/api/types';
import { Button } from '@/shared/ui/Button';
import { useToast } from '@/shared/ui/Toast';
import { ARCHIVED_WORD } from '@/shared/ui/archive';

/**
 * Закреплённая строка «Сейчас: … — в архиве» (issue #1185). Текущее значение пикер обычно не
 * показывает, но архивное обязан: в списке его нет, и без этой строки открытый пикер выглядел бы
 * так, будто выбранной записи не существует. Нажатие — «оставить как есть».
 */
export function RefPickerCurrentArchived({ name, onKeep }: { name: string; onKeep: () => void }) {
  return (
    <button type="button" onClick={onKeep}
      title="Оставить как есть. После замены выбрать эту запись заново будет нельзя."
      className="w-full flex items-center gap-2 px-3 py-2 text-sm text-left rounded-md border border-stroke bg-base hover:bg-brand-subtle transition-colors">
      <Archive size={13} className="text-fg3 shrink-0" />
      <span className="text-fg3 shrink-0">Сейчас:</span>
      <span className="flex-1 font-medium text-fg1 truncate">{name}</span>
      <span className="text-[11px] text-fg3 shrink-0">{ARCHIVED_WORD}</span>
    </button>
  );
}

/**
 * Раздел «В архиве» под действующими записями — ответ на «не нашёл в списке» (issue #1185).
 *
 * <p>Показывается только при набранном запросе: без него это был бы второй список всего архива под
 * каждым выбором. Строки — НЕ варианты выбора: у них нет <code>role="option"</code>, стрелки и
 * Enter по ним не ходят, а действие — отдельная кнопка. Случайно вернуть запись из архива нельзя,
 * потому что возврат меняет справочник для всех.</p>
 *
 * <p>Без права вести общие данные кнопки нет, а текст говорит, к кому идти: отключённая кнопка без
 * объяснения — кнопка в никуда.</p>
 */
export function RefPickerArchive({ entries, onReturned, selectsAtOnce }: {
  entries: CommonDataEntry[];
  /** Запись возвращена из архива — её можно выбирать как действующую. */
  onReturned: (entry: CommonDataEntry) => void;
  /**
   * Встанет ли запись в поле сразу после возврата. Нет — впереди ещё вопрос (вариант union'а), и
   * кнопка зовётся «Вернуть из архива»: возврат необратим для всех, а выбор может не состояться.
   */
  selectsAtOnce: (entry: CommonDataEntry) => boolean;
}) {
  const unarchive = useSetCommonDataArchive();
  const canReturn = useCan().permission('core.catalog.edit');
  const toast = useToast();

  async function giveBack(entry: CommonDataEntry) {
    try {
      await unarchive.mutateAsync({ id: entry.id, archived: false });
    } catch (e) {
      // Не вернулась — не выбираем: ссылка на запись, оставшуюся в архиве, была бы новой ссылкой
      // на то, что из выбора убрали.
      toast.apiError(e, 'Запись не возвращена из архива');
      return;
    }
    toast.success(`Запись «${entry.displayName}» возвращена из архива.`);
    onReturned(entry);
  }

  if (entries.length === 0) return null;
  return (
    <div>
      <p className="text-xs font-medium text-fg3 uppercase tracking-wide mb-2 flex items-center gap-1.5">
        <Archive size={12} className="shrink-0" /> В архиве
      </p>
      <ul className="space-y-1 max-h-40 overflow-y-auto">
        {entries.map(entry => (
          <li key={entry.id} className="flex items-center gap-3 px-3 py-1.5 text-sm rounded-md">
            <Archive size={13} className="text-fg4 shrink-0" />
            <span className="flex-1 text-fg2 truncate">{entry.displayName}</span>
            {canReturn && (
              <Button variant="tonal" size="sm" icon={<ArchiveRestore size={13} />}
                loading={unarchive.isPending && unarchive.variables?.id === entry.id}
                disabled={unarchive.isPending} onClick={() => void giveBack(entry)}>
                {selectsAtOnce(entry) ? 'Вернуть и выбрать' : 'Вернуть из архива'}
              </Button>
            )}
          </li>
        ))}
      </ul>
      {!canReturn && (
        <p className="text-xs text-fg3 mt-1.5">Вернуть из архива может тот, кто ведёт общие данные.</p>
      )}
    </div>
  );
}
