import { useId } from 'react';
import { CountText } from '@/shared/ui/CountText';
import { ListGroupHeader } from '@/shared/ui/ListGroupHeader';
import { ListItem } from '@/shared/ui/ListItem';
import { ListOption } from '@/shared/ui/ListOption';
import type { CommonDataEntry, CatalogScope } from '@/shared/api/types';
import { SCOPE_LABELS } from '@/shared/api/types';
import type { ScopeGroup } from './catalogGroups';

/**
 * Разметка групп каталога — общая для обоих диалогов выбора; правила группировки см. useScopeGroups.
 *
 * <p>Группа — заголовок `ListGroupHeader` (кнопка `aria-expanded`, до неё доходит Tab) и под ним СВОЙ listbox,
 * подписанный заголовком ([UI-87], Р7): кнопка внутри listbox была бы его запрещённым ребёнком. Стрелки поля
 * поиска заголовков не касаются — они ходят по вариантам раскрытых групп.</p>
 */
export function ScopeGroupList({
  groups, isExpanded, toggle, isActive, onHover, onSelect, optionIdOf, listboxIdOf, hintOf, maxHeight = 'max-h-64',
}: {
  groups: ScopeGroup[];
  isExpanded: (scope: CatalogScope) => boolean;
  toggle: (scope: CatalogScope) => void;
  isActive?: (entry: CommonDataEntry) => boolean;
  onHover?: (entry: CommonDataEntry) => void;
  onSelect: (entry: CommonDataEntry) => void;
  /**
   * Идентификатор строки как ОПЦИИ клавиатурного списка. Его наличие и означает, что вызывающий
   * такой список ведёт: строки — варианты `ListOption` в listbox группы, а поле поиска адресует активный
   * через <code>aria-activedescendant</code>.
   *
   * <p>Без него строки остаются обычными строками списка (`ListItem`). Иначе скринридер объявлял бы список
   * невыбранных опций, между которыми некуда перейти: ни активного элемента, ни стрелок — так это и выглядело
   * бы в «Выбрать документ…», где никакой навигации нет.</p>
   */
  optionIdOf?: (entry: CommonDataEntry) => string | undefined;
  /** Идентификатор listbox группы — на него показывает `aria-controls` поля поиска. Нужен вместе с `optionIdOf`. */
  listboxIdOf?: (scope: CatalogScope) => string;
  /** Правая метка строки — например «куда ляжет» для union'а. */
  hintOf?: (entry: CommonDataEntry) => string | null;
  maxHeight?: string;
}) {
  const ids = useId();
  return (
    <div className={`-mx-4 ${maxHeight} overflow-y-auto`}>
      {groups.map(g => {
        const expanded = isExpanded(g.scope);
        const headerId = `${ids}-${g.scope}`;
        return (
          <div key={g.scope}>
            <ListGroupHeader id={headerId} label={SCOPE_LABELS[g.scope]} count={<CountText>{g.entries.length}</CountText>}
              open={expanded} onToggle={() => toggle(g.scope)} />
            {expanded && (optionIdOf ? (
              <div role="listbox" id={listboxIdOf?.(g.scope)} aria-labelledby={headerId}>
                {g.entries.map(entry => (
                  <ListOption key={entry.id} id={optionIdOf(entry) ?? `${ids}-${entry.id}`} headline={entry.displayName}
                    trailing={hintOf?.(entry)} active={isActive?.(entry) ?? false}
                    onHover={() => onHover?.(entry)} onChoose={() => onSelect(entry)} />
                ))}
              </div>
            ) : g.entries.map(entry => (
              <ListItem key={entry.id} headline={entry.displayName} trailing={hintOf?.(entry)} onClick={() => onSelect(entry)} />
            )))}
          </div>
        );
      })}
    </div>
  );
}
