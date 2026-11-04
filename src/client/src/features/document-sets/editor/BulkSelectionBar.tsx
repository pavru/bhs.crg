import type { ReactNode } from 'react';
import { LinkIcon } from '@/shared/ui/icons';
import { SelectionBar } from '@/shared/ui/SelectionBar';
import type { CatalogScope } from '@/shared/api/types';
import { LINK_SCOPE_CHOICES } from './linkScopeChoices';
import { scopeBreakdownText } from '@/features/quality-docs/linkScopes';
import type { MaterialQualityLink } from '@/shared/api/qualityDocs';

/**
 * Массовое действие над выбранными материалами — контекстная шапка выбора (Р8, sharedui-md3-17): пока выбор не
 * пуст, она стоит на месте шапки вкладки (`children`), а не липкой панелью у низа списка. Только при непустом
 * выборе (issue #680): постоянно висевшая выключенная «Связать выбранные (0)» была единственным местом на
 * экране, где произносилось слово «Связать», и обучала, что работа делается отсюда; теперь то же слово стоит
 * в каждой непривязанной строке, а колонка чекбоксов остаётся сигналом массового пути.
 */
export function BulkSelectionBar({ selected, setSelected, openPickerForSelected, scope, widerThanSelector, children }: {
  selected: Set<string>;
  setSelected: (v: Set<string>) => void;
  openPickerForSelected: () => void;
  /** Выбранная область связи: пока стоит шапка выбора, её селектора не видно — предупреждение называет её словом. */
  scope: CatalogScope;
  widerThanSelector: MaterialQualityLink[];
  /** Шапка вкладки, пока ничего не выбрано. */
  children: ReactNode;
}) {
  return (
    <SelectionBar count={selected.size} onClear={() => setSelected(new Set())}
      actions={[{ label: 'Связать выбранные', icon: LinkIcon, onClick: openPickerForSelected, variant: 'filled' }]}
      // Вкладка прокручивается в области с отступом `py-4`: прилипаем к её краю, а не под отступом.
      stickAt="-top-4"
      // Часть выбранных уже связана ШИРЕ, чем выбрано в селекторе, и правка уйдёт на их уровень — за
      // пределы этого комплекта. В строке уровень виден значком, но на сотне строк его никто не
      // пересчитывает, а шапка говорит только «Выбрано: N». Показываем здесь, до нажатия: не диалогом —
      // предупреждение, на которое нельзя ответить «нет», приучает жать «да».
      note={widerThanSelector.length > 0 && <>
        Шире области «{LINK_SCOPE_CHOICES[scope]}» — {widerThanSelector.length} ({scopeBreakdownText(widerThanSelector)}):
        {' '}документ сменится на уровне самой связки, то есть и в других комплектах.
      </>}>
      {children}
    </SelectionBar>
  );
}
