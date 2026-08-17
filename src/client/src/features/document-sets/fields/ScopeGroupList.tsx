import { ChevronDown, ChevronRight } from 'lucide-react';
import type { CommonDataEntry, CatalogScope } from '@/shared/api/types';
import { SCOPE_LABELS } from '@/shared/api/types';
import { SCOPE_COLORS } from './constants';
import type { ScopeGroup } from './catalogGroups';

/** Разметка групп каталога — общая для обоих диалогов выбора; правила группировки см. useScopeGroups. */
export function ScopeGroupList({
  groups, isExpanded, toggle, isActive, onHover, onSelect, optionIdOf, hintOf, maxHeight = 'max-h-64',
}: {
  groups: ScopeGroup[];
  isExpanded: (scope: CatalogScope) => boolean;
  toggle: (scope: CatalogScope) => void;
  isActive?: (entry: CommonDataEntry) => boolean;
  onHover?: (entry: CommonDataEntry) => void;
  onSelect: (entry: CommonDataEntry) => void;
  /**
   * Идентификатор строки как ОПЦИИ клавиатурного списка. Его наличие и означает, что вызывающий
   * такой список ведёт: строки получают <code>role="option"</code> с <code>aria-selected</code>,
   * а поле поиска адресует активную через <code>aria-activedescendant</code>.
   *
   * <p>Без него строки остаются обычными кнопками. Иначе скринридер объявлял бы список
   * невыбранных опций, между которыми некуда перейти: ни владеющего <code>listbox</code>, ни
   * активного элемента, ни стрелок — так это и выглядело бы в «Выбрать документ…», где никакой
   * навигации нет.</p>
   */
  optionIdOf?: (entry: CommonDataEntry) => string | undefined;
  /** Правая метка строки — например «куда ляжет» для union'а. */
  hintOf?: (entry: CommonDataEntry) => string | null;
  maxHeight?: string;
}) {
  const asOptions = !!optionIdOf;
  return (
    <div className={`space-y-1 ${maxHeight} overflow-y-auto`}>
      {groups.map(g => {
        const expanded = isExpanded(g.scope);
        return (
          <div key={g.scope}>
            {/* Заголовок группы = scope-бейдж + счётчик; сворачиваемая секция (a11y-кнопка). */}
            <button type="button" onClick={() => toggle(g.scope)} aria-expanded={expanded}
              className="w-full flex items-center gap-2 px-1 py-1.5 text-left rounded-md hover:bg-base transition-colors">
              {expanded ? <ChevronDown size={13} className="text-fg4 shrink-0" /> : <ChevronRight size={13} className="text-fg4 shrink-0" />}
              <span className={`text-xs px-1.5 py-0.5 rounded font-medium shrink-0 ${SCOPE_COLORS[g.scope]}`}>
                {SCOPE_LABELS[g.scope]}
              </span>
              <span className="text-xs text-fg4">{g.entries.length}</span>
            </button>
            {expanded && (
              <div className="space-y-0.5 pl-1.5">
                {g.entries.map(entry => {
                  const on = isActive?.(entry) ?? false;
                  const hint = hintOf?.(entry) ?? null;
                  return (
                    <button key={entry.id} type="button"
                      role={asOptions ? 'option' : undefined}
                      aria-selected={asOptions ? on : undefined}
                      id={optionIdOf?.(entry)}
                      onMouseEnter={() => onHover?.(entry)} onClick={() => onSelect(entry)}
                      className={`w-full flex items-center px-3 py-2 text-sm text-left rounded-md transition-colors ${
                        on ? 'bg-tonal text-on-tonal' : 'hover:bg-brand-subtle'}`}>
                      <span className={`flex-1 font-medium truncate ${on ? 'text-on-tonal' : 'text-fg1'}`}>
                        {entry.displayName}
                      </span>
                      {hint && (
                        <span className={`text-[11px] shrink-0 ml-2 truncate max-w-[45%] ${on ? 'text-on-tonal' : 'text-fg4'}`}>
                          {hint}
                        </span>
                      )}
                    </button>
                  );
                })}
              </div>
            )}
          </div>
        );
      })}
    </div>
  );
}
