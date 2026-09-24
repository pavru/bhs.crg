import { Loader2, Search } from 'lucide-react';
import { Select, SelectItem } from '@/shared/ui/Select';
import type { CatalogScope } from '@/shared/api/types';

/**
 * Шапка вкладки: обновление материалов, счётчики и выбор области связи (issue #1032 — вынесено из
 * вкладки). Уровни — ровно те, что разрешает резолвер; уровень без известного id не предлагаем,
 * привязку было бы некуда положить.
 */
export function ScopeToolbar({ isFetching, refetch, materialsCount, linkedCount, scope, setScope, sectionId, constructionId }: {
  isFetching: boolean;
  refetch: () => void;
  materialsCount: number;
  linkedCount: number;
  scope: CatalogScope;
  setScope: (s: CatalogScope) => void;
  sectionId: string | null;
  constructionId: string | null;
}) {
  return (
    <div className="flex items-center gap-3 flex-wrap">
      <button onClick={() => refetch()} disabled={isFetching}
        className="flex items-center gap-2 text-sm px-3 py-2 rounded-md bg-muted text-fg2 disabled:opacity-50">
        {isFetching ? <Loader2 size={14} className="animate-spin" /> : <Search size={14} />} Обновить материалы
      </button>
      <span className="text-xs text-fg4">{materialsCount} материалов · привязано {linkedCount}</span>
      <div className="ml-auto flex items-center gap-2">
        <label className="text-xs text-fg3">Область связи:</label>
        {/* Четыре уровня (issue #587) — ровно те, что разрешает резолвер. Раньше их было два, и
            «общая» стояла по умолчанию: узкая ошибка обратима (связка не нашлась), широкая тиха
            (чужой сертификат подставился в чужой документ). Уровень без известного id не
            предлагаем — привязку было бы некуда положить. */}
        <Select value={scope} onValueChange={v => setScope(v as CatalogScope)}
          aria-label="Область связи" className="w-56">
          <SelectItem value="Set">Только этот комплект</SelectItem>
          {sectionId && <SelectItem value="Section">Весь раздел</SelectItem>}
          {constructionId && <SelectItem value="Construction">Вся стройка</SelectItem>}
          {/* Слово «Система» — из общего словаря областей (issue #649): экран контроля называет
              этот уровень так же, и выбирать одним словом, а читать другое человек не должен. */}
          <SelectItem value="System">Все стройки (Система)</SelectItem>
        </Select>
      </div>
    </div>
  );
}
