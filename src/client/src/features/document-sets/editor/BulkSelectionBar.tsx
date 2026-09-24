import { Link2 } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { scopeBreakdownText } from '@/features/quality-docs/linkScopes';
import type { MaterialQualityLink } from '@/shared/api/qualityDocs';

/**
 * Панель массового действия — только при непустом выборе (issue #680). Постоянно висевшая
 * выключенная «Связать выбранные (0)» была единственным местом на экране, где произносилось слово
 * «Связать», и обучала, что работа делается отсюда; теперь то же слово стоит в каждой непривязанной
 * строке, а колонка чекбоксов остаётся сигналом массового пути.
 */
export function BulkSelectionBar({ selected, setSelected, openPickerForSelected, widerThanSelector }: {
  selected: Set<string>;
  setSelected: (v: Set<string>) => void;
  openPickerForSelected: () => void;
  widerThanSelector: MaterialQualityLink[];
}) {
  return (
    <div className="sticky bottom-0 rounded-md border border-stroke bg-surface px-3 py-2 shadow-sm">
      <div className="flex items-center gap-2">
        <span className="text-sm text-fg2">Выбрано: {selected.size}</span>
        <Button variant="filled" size="sm" onClick={openPickerForSelected} icon={<Link2 size={13} />}>
          Связать выбранные ({selected.size})
        </Button>
        <button onClick={() => setSelected(new Set())}
          className="ml-auto text-xs text-fg4 hover:text-fg2">Снять выбор</button>
      </div>
      {/* Часть выбранных уже связана ШИРЕ, чем выбрано в селекторе, и правка уйдёт на их
          уровень — за пределы этого комплекта. В строке уровень виден значком, но на сотне
          строк его никто не пересчитывает, а панель говорит только «Выбрано: N». Показываем
          здесь, до нажатия: не диалогом — предупреждение, на которое нельзя ответить «нет»,
          приучает жать «да». */}
      {widerThanSelector.length > 0 && (
        <p className="mt-1.5 text-xs text-warning">
          Шире выбранного уровня — {widerThanSelector.length} ({scopeBreakdownText(widerThanSelector)}):
          {' '}документ сменится на уровне самой связки, то есть и в других комплектах.
        </p>
      )}
    </div>
  );
}
