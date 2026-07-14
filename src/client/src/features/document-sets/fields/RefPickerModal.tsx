import { useState, useEffect } from 'react';
import { FileText, ChevronDown, ChevronRight } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { useCommonDataForScope } from '@/shared/api/commonData';
import type {
  CommonDataEntry, DocumentInstance, DocumentType, FieldRef, CatalogScope,
} from '@/shared/api/types';
import { SCOPE_LABELS } from '@/shared/api/types';
import { resolveEffectiveFields, isSubtypeOf, type SchemaField } from '@/shared/api/schema';
import { SCOPE_COLORS } from './constants';

export function RefPickerModal({
  open, onOpenChange, compositeType,
  setId, scope, scopeId,
  otherInstances = [], allDocTypes, onSelect,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  compositeType: DocumentType | null;
  setId?: string;
  scope?: CatalogScope;
  scopeId?: string | null;
  otherInstances?: DocumentInstance[];
  allDocTypes: DocumentType[];
  onSelect: (ref: FieldRef) => void;
}) {
  const [search, setSearch] = useState('');

  // Единый резолв всей цепочки скопов (issue #82): комплект-контекст → (Set, setId), иначе (scope, scopeId).
  // for-scope сам поднимается по родителям (Раздел→Стройка→Система), поэтому объекты более широких
  // уровней видны из раздел/строечных контекстов (раньше запасной путь их пропускал).
  const effScope: CatalogScope | undefined = setId ? 'Set' : scope;
  const effScopeId = setId ?? scopeId;
  const { data: catalogEntries = [] } = useCommonDataForScope({
    scope: effScope, scopeId: effScopeId, enabled: open && !!effScope,
  });

  const filtered = catalogEntries.filter(e => {
    if (compositeType && !isSubtypeOf(e.compositeTypeId, compositeType.id, allDocTypes)) return false;
    return e.displayName.toLowerCase().includes(search.toLowerCase());
  });

  // Группировка по scope: ближайший уровень (Комплект) вверху, дальние — ниже. Пустые группы скрыты.
  const SCOPE_ORDER: CatalogScope[] = ['Set', 'Section', 'Construction', 'System'];
  const groups = SCOPE_ORDER
    .map(s => ({ scope: s, entries: filtered.filter(e => e.scope === s) }))
    .filter(g => g.entries.length > 0);

  const searching = search.trim().length > 0;
  const firstScope = groups[0]?.scope; // ближайшая НЕпустая группа — раскрыта по умолчанию
  // Ручные переопределения сворачивания (действуют, когда поиск пуст). При поиске все группы с
  // совпадениями раскрыты (иначе матч спрятался бы за свёрнутой группой).
  const [collapseOverride, setCollapseOverride] = useState<Partial<Record<CatalogScope, boolean>>>({});
  const isExpanded = (scope: CatalogScope) =>
    searching ? true : (collapseOverride[scope] ?? scope === firstScope);
  const toggleGroup = (scope: CatalogScope) =>
    setCollapseOverride(o => ({ ...o, [scope]: !isExpanded(scope) }));

  const docSources = compositeType && setId
    ? otherInstances.flatMap(inst => {
        const dt = allDocTypes.find(t => t.id === inst.documentTypeId);
        if (!dt) return [];
        const fields = resolveEffectiveFields(dt, allDocTypes).filter(
          f => f.type === 'complex' && f.typeId === compositeType.id,
        );
        return fields.map(f => ({ inst, dt, field: f }));
      })
    : [];

  // Плоский список навигируемых опций (issue #107 F5): видимые (в раскрытых группах) записи
  // каталога + источники-документы — в порядке отображения. Стрелки/Enter ходят по ним.
  type RpOption =
    | { type: 'catalog'; entry: CommonDataEntry }
    | { type: 'doc'; inst: DocumentInstance; dt: DocumentType; field: SchemaField };
  const options: RpOption[] = [
    ...groups.flatMap(g => isExpanded(g.scope) ? g.entries.map(entry => ({ type: 'catalog' as const, entry })) : []),
    ...docSources.map(d => ({ type: 'doc' as const, ...d })),
  ];
  const [active, setActive] = useState(0);
  useEffect(() => { setActive(0); }, [search, collapseOverride]);
  const optKey = (o: RpOption) => o.type === 'catalog' ? `c:${o.entry.id}` : `d:${o.inst.id}-${o.field.key}`;
  const indexByKey = new Map(options.map((o, i) => [optKey(o), i]));
  function activate(o: RpOption) {
    if (o.type === 'catalog') selectCatalog(o.entry);
    else selectDocument(o.inst, o.dt, o.field);
  }
  function onKey(e: React.KeyboardEvent) {
    if (e.key === 'ArrowDown') { e.preventDefault(); setActive(a => Math.min(a + 1, options.length - 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setActive(a => Math.max(a - 1, 0)); }
    else if (e.key === 'Enter') { e.preventDefault(); const o = options[active]; if (o) activate(o); }
  }

  function selectCatalog(entry: CommonDataEntry) {
    onSelect({
      $ref: 'catalog',
      entryId: entry.id,
      displayName: entry.displayName,
      scope: entry.scope,
    });
    onOpenChange(false);
  }

  function selectDocument(inst: DocumentInstance, dt: DocumentType, field: SchemaField) {
    onSelect({
      $ref: 'document',
      instanceId: inst.id,
      fieldKey: field.key,
      displayName: `${dt.name} → ${field.title}`,
    });
    onOpenChange(false);
  }

  return (
    <Modal open={open} onOpenChange={onOpenChange} title="Выбрать объект">
      <div className="space-y-4">
        <input
          value={search} onChange={e => setSearch(e.target.value)} onKeyDown={onKey}
          placeholder="Поиск…" autoFocus role="combobox" aria-expanded
          aria-activedescendant={options.length ? `rp-opt-${active}` : undefined}
          className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface"
        />

        {groups.length > 0 && (
          <div>
            <p className="text-xs font-medium text-fg3 uppercase tracking-wide mb-2">
              Каталог общих данных
            </p>
            <div className="space-y-1 max-h-64 overflow-y-auto">
              {groups.map(g => {
                const expanded = isExpanded(g.scope);
                return (
                  <div key={g.scope}>
                    {/* Заголовок группы = scope-бейдж + счётчик; сворачиваемая секция (a11y-кнопка). */}
                    <button type="button" onClick={() => toggleGroup(g.scope)} aria-expanded={expanded}
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
                          const gi = indexByKey.get(`c:${entry.id}`) ?? -1;
                          const on = gi === active;
                          return (
                            <button key={entry.id} type="button" role="option" aria-selected={on} id={`rp-opt-${gi}`}
                              onMouseEnter={() => setActive(gi)} onClick={() => selectCatalog(entry)}
                              className={`w-full flex items-center px-3 py-2 text-sm text-left rounded-md transition-colors ${
                                on ? 'bg-tonal text-on-tonal' : 'hover:bg-brand-subtle'}`}>
                              <span className={`flex-1 font-medium truncate ${on ? 'text-on-tonal' : 'text-fg1'}`}>{entry.displayName}</span>
                            </button>
                          );
                        })}
                      </div>
                    )}
                  </div>
                );
              })}
            </div>
          </div>
        )}

        {docSources.length > 0 && (
          <div>
            <p className="text-xs font-medium text-fg3 uppercase tracking-wide mb-2">
              Из других документов комплекта
            </p>
            <div className="space-y-1">
              {docSources.map(({ inst, dt, field }) => {
                const gi = indexByKey.get(`d:${inst.id}-${field.key}`) ?? -1;
                const on = gi === active;
                return (
                  <button key={`${inst.id}-${field.key}`} type="button" role="option" aria-selected={on} id={`rp-opt-${gi}`}
                    onMouseEnter={() => setActive(gi)} onClick={() => selectDocument(inst, dt, field)}
                    className={`w-full flex items-center gap-3 px-3 py-2 text-sm text-left rounded-md transition-colors ${
                      on ? 'bg-tonal text-on-tonal' : 'hover:bg-brand-subtle'}`}>
                    <FileText size={14} className={`shrink-0 ${on ? 'text-on-tonal' : 'text-fg4'}`} />
                    <span className={`flex-1 font-medium truncate ${on ? 'text-on-tonal' : 'text-fg1'}`}>
                      {dt.name} → {field.title}
                    </span>
                  </button>
                );
              })}
            </div>
          </div>
        )}

        {filtered.length === 0 && docSources.length === 0 && (
          <p className="text-sm text-fg4 text-center py-4">
            Нет объектов доступных для ссылки.
            <br />
            <span className="text-xs">Добавьте записи в каталог общих данных.</span>
          </p>
        )}
      </div>
    </Modal>
  );
}
