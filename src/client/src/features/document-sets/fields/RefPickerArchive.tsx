import { Icon } from '@/shared/ui/Icon';
import { Archive, Unarchive } from '@/shared/ui/icons';
import { useSetCommonDataArchive } from '@/shared/api/commonData';
import { useCan } from '@/shared/api/access';
import type { CommonDataEntry } from '@/shared/api/types';
import { Button } from '@/shared/ui/Button';
import { useSnackbar } from '@/shared/ui/Snackbar';
import { ARCHIVED_WORD } from '@/shared/ui/archive';
import { ListItem } from '@/shared/ui/ListItem';

/**
 * Закреплённая строка «Сейчас: … — в архиве» (issue #1185). Текущее значение пикер обычно не
 * показывает, но архивное обязан: в списке его нет, и без этой строки открытый пикер выглядел бы
 * так, будто выбранной записи не существует. Нажатие — «оставить как есть».
 */
export function RefPickerCurrentArchived({ name, onKeep }: { name: string; onKeep: () => void }) {
  // Строка списка, а не своя кнопка с рамкой ([UI-84]): та же форма, что у записей под ней.
  return (
    <div className="-mx-4">
      <ListItem icon={Archive} headline={name} supporting={`Сейчас · ${ARCHIVED_WORD}`} onClick={onKeep}
        hint="Оставить как есть. После замены выбрать эту запись заново будет нельзя." />
    </div>
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
  const toast = useSnackbar();

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
      <p className="text-title-small text-on-surface-variant mb-2 flex items-center gap-1.5">
        <Icon icon={Archive} size={18} className="shrink-0" /> В архиве
      </p>
      {/* Строки без своей цели (`passive`): действие — кнопка у края, сама строка не нажимается ([UI-84]). */}
      <div className="-mx-4 max-h-40 overflow-y-auto">
        {entries.map(entry => (
          <ListItem key={entry.id} passive icon={Archive} headline={entry.displayName}
            action={canReturn && (
              <Button variant="text" icon={<Icon icon={Unarchive} size={18} />}
                loading={unarchive.isPending && unarchive.variables?.id === entry.id}
                disabled={unarchive.isPending} onClick={() => void giveBack(entry)}>
                {selectsAtOnce(entry) ? 'Вернуть и выбрать' : 'Вернуть из архива'}
              </Button>
            )} />
        ))}
      </div>
      {!canReturn && (
        <p className="text-body-small text-on-surface-variant mt-1.5">Вернуть из архива может тот, кто ведёт общие данные.</p>
      )}
    </div>
  );
}
