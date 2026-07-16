import { useState, useEffect } from 'react';
import { FileText, Database, Link2, Unlink, AlertTriangle } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import type { CatalogScope, DocumentType } from '@/shared/api/types';

// Базовый экземпляр (issue #71/#73): объект дочернего типа наследуется от базы — документа комплекта
// ЛИБО записи общих данных (по цепочке типов-предков и скоп-близости). Единый пикер + общие хелперы
// кандидатов — переиспользуются редактором документов, каталогом и системным каталогом (шаг 2 #73).

export type BaseCandidateKind = 'instance' | 'catalog';

export interface BaseCandidate {
  kind: BaseCandidateKind;
  id: string;
  name: string;
  typeId: string;
  tier: number;        // скоп-уровень: 0 комплект, 1 раздел, 2 стройка, 3 система
  scopeLabel: string;  // «Комплект»/«Раздел»/«Стройка»/«Система»
  dist: number;        // дистанция наследования: 0 прямой родитель, дальше — больше
  proxy?: boolean;     // issue #89: кандидат того же типа — «роль на реальный объект», а не наследование от родителя
  targetIsProxy?: boolean; // выбранный «реальный» сам является прокси (цепочка ссылок — data-smell)
}

export const SCOPE_TIER: Record<CatalogScope, number> = { Set: 0, Section: 1, Construction: 2, System: 3 };

/** Идентификаторы типов-предков по цепочке parentId (по возрастанию дистанции: [родитель, дед, …]). */
export function ancestorTypeIds(docType: DocumentType | undefined, allDocTypes: DocumentType[]): string[] {
  const ids: string[] = [];
  const seen = new Set<string>();
  let cur = docType?.parentId ?? undefined;
  while (cur && !seen.has(cur)) {
    seen.add(cur); ids.push(cur);
    cur = allDocTypes.find(dt => dt.id === cur)?.parentId ?? undefined;
  }
  return ids;
}

/** Толерантный разбор _baseRef: {kind,id} (issue #71) или голая строка-id (legacy = catalog/запись). */
export function parseBaseRef(raw: unknown): { kind: BaseCandidateKind; id: string } | undefined {
  if (typeof raw === 'string') return raw ? { kind: 'catalog', id: raw } : undefined;
  if (raw && typeof raw === 'object' && 'id' in raw) {
    const r = raw as { kind?: string; id?: string };
    if (r.id) return { kind: r.kind === 'instance' ? 'instance' : 'catalog', id: r.id };
  }
  return undefined;
}

/**
 * Единый пикер базового экземпляра — презентационный: получает уже готовый (обычно отсортированный
 * по близости) список кандидатов и вызывает onSelect. Вычисление и формат хранения `_baseRef` —
 * ответственность вызывающего (документ пишет {kind,id}; каталог/система — голый id).
 */
export function BaseCandidatePicker({ open, onOpenChange, candidates, onSelect }: {
  open: boolean; onOpenChange: (o: boolean) => void;
  candidates: BaseCandidate[]; onSelect: (c: BaseCandidate) => void;
}) {
  const [search, setSearch] = useState('');
  const [active, setActive] = useState(0);
  const filtered = candidates.filter(c => c.name.toLowerCase().includes(search.toLowerCase()));
  // issue #89: две именованные группы — «роль на реальный объект того же типа» и наследование от базового типа.
  const proxyCands = filtered.filter(c => c.proxy);
  const baseCands = filtered.filter(c => !c.proxy);
  // Единый порядок для клавиатурной навигации (issue #107 F5): proxy-группа, затем базовые.
  const ordered = [...proxyCands, ...baseCands];
  useEffect(() => { setActive(0); }, [search]);

  function pick(c: BaseCandidate) { onSelect(c); onOpenChange(false); }
  function onKey(e: React.KeyboardEvent) {
    if (e.key === 'ArrowDown') { e.preventDefault(); setActive(a => Math.min(a + 1, ordered.length - 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setActive(a => Math.max(a - 1, 0)); }
    else if (e.key === 'Enter') { e.preventDefault(); const c = ordered[active]; if (c) pick(c); }
  }

  const row = (c: BaseCandidate, gi: number) => {
    const on = gi === active;
    return (
      <button key={`${c.kind}:${c.id}`} type="button" role="option" aria-selected={on} id={`bcp-opt-${gi}`}
        onMouseEnter={() => setActive(gi)} onClick={() => pick(c)}
        className={`w-full flex items-center gap-3 px-3 py-2 text-sm text-left rounded-md transition-colors ${
          on ? 'bg-tonal text-on-tonal' : 'hover:bg-brand-subtle'}`}>
        {c.proxy
          ? <Link2 size={13} className={`shrink-0 ${on ? 'text-on-tonal' : 'text-brand'}`} />
          : c.kind === 'instance'
            ? <FileText size={13} className={`shrink-0 ${on ? 'text-on-tonal' : 'text-brand'}`} />
            : <Database size={13} className={`shrink-0 ${on ? 'text-on-tonal' : 'text-brand'}`} />}
        <span className={`flex-1 font-medium truncate ${on ? 'text-on-tonal' : 'text-fg1'}`}>{c.name}</span>
        {c.targetIsProxy && <span className="text-[10px] text-warning shrink-0">роль</span>}
        <span className={`text-[11px] shrink-0 ${on ? 'text-on-tonal/80' : 'text-fg4'}`}>{c.scopeLabel}</span>
      </button>
    );
  };

  return (
    <Modal open={open} onOpenChange={onOpenChange} title="Базовый экземпляр / роль">
      <div className="space-y-4">
        <input value={search} onChange={e => setSearch(e.target.value)} onKeyDown={onKey}
          placeholder="Поиск…" autoFocus role="combobox" aria-expanded aria-controls="bcp-listbox"
          aria-activedescendant={ordered.length ? `bcp-opt-${active}` : undefined}
          className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface" />
        {filtered.length === 0 ? (
          <p className="text-sm text-fg4 text-center py-4">Нет подходящих кандидатов.</p>
        ) : (
          <div id="bcp-listbox" role="listbox" aria-label="Кандидаты" className="space-y-3 max-h-72 overflow-y-auto">
            {proxyCands.length > 0 && (
              <div className="space-y-1">
                <p className="text-[11px] font-semibold text-fg3 uppercase tracking-wide px-1">Роль на реальный объект (тот же тип)</p>
                {proxyCands.map((c, i) => row(c, i))}
              </div>
            )}
            {baseCands.length > 0 && (
              <div className="space-y-1">
                {proxyCands.length > 0 && <p className="text-[11px] font-semibold text-fg3 uppercase tracking-wide px-1">Базовый тип</p>}
                {baseCands.map((c, j) => row(c, proxyCands.length + j))}
              </div>
            )}
          </div>
        )}
      </div>
    </Modal>
  );
}

/**
 * Панель «Базовый экземпляр» (issue #73, шаг 3) — единый блок для всех форм объекта составного типа
 * (редактор документов, каталог, системный каталог). Показывает выбранную базу / кнопку выбора /
 * состояние «недоступна», содержит пикер. Вычисление кандидатов и формат хранения `_baseRef` —
 * ответственность вызывающего (см. BaseCandidatePicker).
 */
export function BaseInstancePanel({ title, candidates, selected, missing = false, manualHint, onSelect, onClear }: {
  title?: string;                       // напр. имя родительского типа — в подзаголовок
  candidates: BaseCandidate[];
  selected: BaseCandidate | undefined;  // выбранная база (по _baseRef), если найдена среди кандидатов
  missing?: boolean;                    // ссылка задана, но кандидат не найден (удалён/вне видимости)
  manualHint?: string;                  // подсказка «без базы все N полей вручную»
  onSelect: (c: BaseCandidate) => void;
  onClear: () => void;
}) {
  const [pickerOpen, setPickerOpen] = useState(false);
  return (
    <div className="rounded-xl border border-stroke bg-surface p-4 space-y-2">
      <p className="text-sm font-medium text-fg1">
        {selected?.proxy ? 'Роль — ссылка на реальный объект' : 'Базовый экземпляр'}
        {title && !selected?.proxy && <span className="font-normal ml-1 text-fg4">({title})</span>}
      </p>
      {selected ? (
        <>
        <div className="flex items-center gap-2 rounded-md border border-brand-subtle bg-brand-subtle px-3 py-2">
          {selected.proxy
            ? <Link2 size={14} className="text-brand shrink-0" />
            : selected.kind === 'instance'
              ? <FileText size={14} className="text-brand shrink-0" />
              : <Database size={14} className="text-brand shrink-0" />}
          <span className="flex-1 text-sm font-medium text-brand-hover truncate">
            {selected.proxy && <span className="text-fg4 font-normal">→ </span>}{selected.name}
          </span>
          <span className="text-[11px] text-fg4 shrink-0">{selected.scopeLabel}</span>
          <button type="button" onClick={onClear} className="text-brand hover:text-danger transition-colors" title="Снять ссылку">
            <Unlink size={13} />
          </button>
        </div>
        {selected.targetIsProxy && (
          <p className="text-[11px] text-warning flex items-center gap-1">
            <AlertTriangle size={11} className="shrink-0" />
            «{selected.name}» — тоже роль. Цепочка ссылок усложняет прослеживание данных.
          </p>
        )}
        </>
      ) : missing ? (
        <div className="flex items-center gap-2 rounded-md border border-warning/40 bg-warning/5 px-3 py-2">
          <span className="flex-1 text-sm text-warning truncate">Базовый экземпляр недоступен (удалён или вне области видимости)</span>
          <button type="button" onClick={onClear} className="text-brand hover:text-danger transition-colors" title="Снять ссылку">
            <Unlink size={13} />
          </button>
        </div>
      ) : (
        <button type="button" onClick={() => setPickerOpen(true)}
          className="flex items-center gap-2 text-sm text-brand hover:text-brand-hover border border-dashed border-brand-subtle rounded-md px-3 py-2 w-full hover:bg-brand-subtle transition-colors">
          <Link2 size={14} />
          Выбрать базовый экземпляр или роль...
        </button>
      )}
      {!selected && !missing && manualHint && <p className="text-xs text-fg4">{manualHint}</p>}
      <BaseCandidatePicker open={pickerOpen} onOpenChange={setPickerOpen} candidates={candidates} onSelect={onSelect} />
    </div>
  );
}
