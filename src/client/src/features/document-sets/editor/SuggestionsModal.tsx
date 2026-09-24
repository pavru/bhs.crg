import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { toggleInSet } from '@/shared/utils/toggleInSet';
import type { LinkSuggestion } from '@/shared/api/qualityDocs';

/**
 * Обзор предложенных связей перед применением (issue #1032 — вынесено из вкладки).
 *
 * Список приходит из двух мест — подсказки библиотеки и подсказки по истории; обзор у них один
 * намеренно (issue #682): два соседних действия с одним смыслом вели себя противоположно.
 */
export function SuggestionsModal({ suggestions, setSuggestions, suggestSel, setSuggestSel, applySuggestions, applying }: {
  suggestions: LinkSuggestion[] | null;
  setSuggestions: (v: LinkSuggestion[] | null) => void;
  suggestSel: Set<string>;
  setSuggestSel: (f: (prev: Set<string>) => Set<string>) => void;
  applySuggestions: () => Promise<void>;
  applying: boolean;
}) {
  return (
    <Modal open={suggestions !== null} onOpenChange={o => { if (!o) setSuggestions(null); }}
      title="Предложенные связи" wide
      footer={
        <div className="flex items-center gap-2">
          <Button variant="filled" onClick={applySuggestions} loading={applying}
            disabled={suggestSel.size === 0}>
            {applying ? 'Применение...' : `Применить выбранные (${suggestSel.size})`}
          </Button>
          <Button variant="text" onClick={() => setSuggestions(null)}>Отмена</Button>
        </div>
      }>
      {suggestions && suggestions.length === 0 ? (
        <p className="text-sm text-fg4 py-4 text-center">
          Подходящих документов не найдено. Свяжите несколько материалов вручную — дальше похожие предложатся автоматически.
        </p>
      ) : (
        <div className="divide-y divide-muted border border-stroke rounded-md max-h-[55vh] overflow-y-auto">
          {(suggestions ?? []).map(s => (
            <label key={s.materialKey} className="flex items-center gap-3 px-3 py-2 text-sm cursor-pointer hover:bg-base">
              <input type="checkbox" checked={suggestSel.has(s.materialKey)}
                onChange={() => setSuggestSel(prev => toggleInSet(prev, s.materialKey))}
                className="w-4 h-4 rounded border-stroke-strong text-brand" />
              <span className="flex-1 truncate text-fg1">{s.materialName}</span>
              <span className="text-fg4">→</span>
              <span className="flex-1 truncate text-brand-hover">{s.docDisplayName}</span>
              <span className="text-xs text-fg4 shrink-0">{Math.round(s.score * 100)}%</span>
            </label>
          ))}
        </div>
      )}
    </Modal>
  );
}
