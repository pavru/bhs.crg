import type { ReactNode } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { LinkIcon } from '@/shared/ui/icons';
import { Button, IconButton } from '@/shared/ui/Button';
import { SelectionBar } from '@/shared/ui/SelectionBar';
import { useMedia, WIDE } from '@/shared/hooks/useMedia';
import { scopeBreakdownText } from '@/features/quality-docs/linkScopes';
import type { MaterialQualityLink } from '@/shared/api/qualityDocs';

/**
 * Массовое действие над выбранными материалами — контекстная шапка выбора (Р8, sharedui-md3-17): пока выбор не
 * пуст, она стоит на месте шапки вкладки (`children`), а не липкой панелью у низа списка. Только при непустом
 * выборе (issue #680): постоянно висевшая выключенная «Связать выбранные (0)» была единственным местом на
 * экране, где произносилось слово «Связать», и обучала, что работа делается отсюда; теперь то же слово стоит
 * в каждой непривязанной строке, а колонка чекбоксов остаётся сигналом массового пути.
 */
export function BulkSelectionBar({ selected, setSelected, openPickerForSelected, widerThanSelector, children }: {
  selected: Set<string>;
  setSelected: (v: Set<string>) => void;
  openPickerForSelected: () => void;
  widerThanSelector: MaterialQualityLink[];
  /** Шапка вкладки, пока ничего не выбрано. */
  children: ReactNode;
}) {
  const wide = useMedia(WIDE);
  return (
    <SelectionBar count={selected.size} onClear={() => setSelected(new Set())}
      actions={wide
        ? <Button variant="filled" onClick={openPickerForSelected} icon={<Icon icon={LinkIcon} size={18} />}>Связать выбранные</Button>
        : <IconButton variant="filled" label="Связать выбранные" onClick={openPickerForSelected} icon={LinkIcon} />}
      // Часть выбранных уже связана ШИРЕ, чем выбрано в селекторе, и правка уйдёт на их уровень — за
      // пределы этого комплекта. В строке уровень виден значком, но на сотне строк его никто не
      // пересчитывает, а шапка говорит только «Выбрано: N». Показываем здесь, до нажатия: не диалогом —
      // предупреждение, на которое нельзя ответить «нет», приучает жать «да».
      note={widerThanSelector.length > 0 && <>
        Шире выбранного уровня — {widerThanSelector.length} ({scopeBreakdownText(widerThanSelector)}):
        {' '}документ сменится на уровне самой связки, то есть и в других комплектах.
      </>}>
      {children}
    </SelectionBar>
  );
}
