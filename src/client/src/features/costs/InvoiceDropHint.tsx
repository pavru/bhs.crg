import { WIDE, useMedia } from '@/shared/hooks/useMedia';
import { wordsOf, type FileKind } from '@/shared/api/fileKinds';
import { Icon } from '@/shared/ui/Icon';
import { Upload } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';

/**
 * Форматы — словами реестра видов файлов (`recognizedKinds`), тот же источник, что у выбора файла. Реестр не
 * пришёл — пустой перечень, и фраза идёт без скобок: «не пришло» не превращается в выдуманный перечень.
 */
function hintText(kinds: FileKind[]): string {
  const named = kinds.length ? ` (${wordsOf(kinds)})` : '';
  return `Перетащите файлы счетов${named} в любое место окна — заведём черновики и распознаем их в фоне, `
    + 'поля останется проверить.';
}

/**
 * Подсказка о броске файлов (shell-07-scan-drop-hint, [COST-8] [COST-31], #1435): умение окна видно до того, как
 * файл потащили. Целью броска служит всё окно (costs-invoice-screen-14), поэтому строка — не площадка: без
 * пунктира, чертой сверху у нижнего края колонки списка, вне её прокрутки (`shrink-0`). С `inline` — та же
 * фраза в пустом списке: частью пустого состояния, а не второй строкой под ним.
 *
 * Только на рабочем столе: на телефоне перетаскивать нечем, а вход там — плавающая кнопка «Счёт из файла».
 * Право и «не идёт загрузка» решает страница. «Выбрать файл» — текстовая кнопка: основная на экране одна, и
 * это «Счёт из файла»; `onPick` открывает тот же выбор файла.
 */
export function InvoiceDropHint({ kinds, onPick, inline = false }: {
  kinds: FileKind[];
  onPick: () => void;
  inline?: boolean;
}) {
  const wide = useMedia(WIDE);
  if (!wide) return null;
  const pick = <Button variant="text" onClick={onPick}>Выбрать файл</Button>;
  if (inline)
    return (
      <div className="space-y-1" data-testid="invoice-drop-hint-inline">
        <p>{hintText(kinds)}</p>
        <div className="-ml-3">{pick}</div>
      </div>
    );
  return (
    <div className="shrink-0 border-t border-outline-variant pl-4 pr-2 pt-2 pb-1" data-testid="invoice-drop-hint">
      <div className="flex items-start gap-2 text-body-small text-on-surface-variant">
        <Icon icon={Upload} size={18} className="shrink-0 text-primary" />
        <p>{hintText(kinds)}</p>
      </div>
      <div className="flex justify-end">{pick}</div>
    </div>
  );
}
