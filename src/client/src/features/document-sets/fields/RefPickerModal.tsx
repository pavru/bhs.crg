import { useState, useEffect } from 'react';
import { FileText, ChevronDown, ChevronRight } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { useCommonDataForScope } from '@/shared/api/commonData';
import type {
  CommonDataEntry, DocumentInstance, DocumentType, FieldRef, CatalogScope,
} from '@/shared/api/types';
import { SCOPE_LABELS } from '@/shared/api/types';
import {
  resolveEffectiveFields, isSubtypeOf, isUnionType, placeInUnion,
  type SchemaField, type UnionPlacement,
} from '@/shared/api/schema';
import { SCOPE_COLORS } from './constants';
import { VariantPicker } from './VariantPicker';

/**
 * Подпись источника: «имя документа → поле», с откатом на имя типа.
 *
 * <p>Раньше подпись строилась от типа — «АОСР → Представитель подрядчика». В комплекте АОСР бывает
 * несколько (у живого их два), и такие строки становились неотличимы: какой именно документ
 * протягивается, из списка не видно, а после выбора не видно и в самой ссылке — та же строка едет
 * в её displayName. Имя экземпляра там, где оно есть, ровно как в InstancePickerModal.</p>
 */
function sourceLabel(inst: DocumentInstance, dt: DocumentType, f: SchemaField): string {
  return `${inst.name || dt.name} → ${f.title}`;
}

/** Значение поля-источника отсутствует: null/undefined, пустая строка, пустой объект или массив. */
function isBlank(v: unknown): boolean {
  if (v == null || v === '') return true;
  if (Array.isArray(v)) return v.length === 0;
  if (typeof v === 'object') return Object.keys(v as object).length === 0;
  return false;
}

export function RefPickerModal({
  open, onOpenChange, compositeType,
  setId, scope, scopeId,
  otherInstances = [], allDocTypes, unionAware = false, onSelect,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  compositeType: DocumentType | null;
  setId?: string;
  scope?: CatalogScope;
  scopeId?: string | null;
  otherInstances?: DocumentInstance[];
  allDocTypes: DocumentType[];
  /**
   * Вызывающий умеет класть значение в ключ варианта union'а (issue #747) — и только тогда пикер
   * предлагает записи типов-ВАРИАНТОВ, а не только самого составного типа.
   *
   * Согласие обязано быть явным, потому что тот же пикер зовёт <code>ComplexCellPicker</code>,
   * который пишет голую ссылку и заворачивать не умеет: расширь мы фильтр молча, union получил бы
   * значение без ключа варианта — инвариант «ровно один ключ» (#320) сломался бы там, где никто не
   * смотрит. Сегодня дыра латентна (union-типы стоят элементами массивов, а не complex-колонками),
   * и открылась бы она тихо.
   */
  unionAware?: boolean;
  /** Второй аргумент — ключ варианта union'а; отсутствует, когда значение кладётся как есть. */
  onSelect: (ref: FieldRef, variantKey?: string) => void;
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

  // Union-режим (issue #747): кандидат подходит, если его тип годится САМОМУ union'у или любому его
  // одиночному варианту. Куда ляжет запись, считаем здесь же и несём до onSelect — второй раз тот же
  // вопрос задавать негде: в обработчике выбора уже нет ни типов, ни цепочки наследования под рукой.
  const unionMode = unionAware && !!compositeType && isUnionType(compositeType, allDocTypes);
  // Мемо по типу записи, а не по записи: решение зависит ТОЛЬКО от типа, а типов на порядки меньше,
  // чем записей. Без него placeInUnion звался бы трижды на каждого кандидата при каждом нажатии
  // клавиши в поиске, и каждый вызов заново разрешает схему union'а и идёт вверх по цепочке.
  const placements = new Map<string, UnionPlacement>();
  const placementOf = (e: CommonDataEntry): UnionPlacement => {
    const cached = placements.get(e.compositeTypeId);
    if (cached) return cached;
    const computed = placeInUnion(e.compositeTypeId, compositeType!, allDocTypes);
    placements.set(e.compositeTypeId, computed);
    return computed;
  };

  const filtered = catalogEntries.filter(e => {
    if (compositeType) {
      const fits = unionMode
        ? placementOf(e).kind !== 'none'
        : isSubtypeOf(e.compositeTypeId, compositeType.id, allDocTypes);
      if (!fits) return false;
    }
    return e.displayName.toLowerCase().includes(search.toLowerCase());
  });

  // Запись, для которой тип не назвал единственного варианта: показываем её вторым шагом со
  // списком вариантов вместо того, чтобы прятать. Прятать нельзя — ничья означает, что два
  // варианта объявлены на один тип (иначе при одиночном наследовании дистанции не совпали бы),
  // то есть это осмысленная схема, а не порча данных, и исправить её из пикера человек не может.
  const [askVariantFor, setAskVariantFor] = useState<CommonDataEntry | null>(null);
  useEffect(() => { if (!open) setAskVariantFor(null); }, [open]);

  /** Подписи вариантов union'а — для метки на кандидате и для второго шага. */
  const variantFields = unionMode
    ? resolveEffectiveFields(compositeType!, allDocTypes).filter(f => f.type === 'complex' || f.type === 'doc-ref')
    : [];
  const variantTitle = (key: string) => variantFields.find(f => f.key === key)?.title ?? key;

  // Метка «куда ляжет» — только когда принимающих слотов больше одного: при единственном варианте
  // она повторяла бы заголовок поля и была бы шумом.
  function variantHint(entry: CommonDataEntry): string | null {
    if (!unionMode || variantFields.length < 2) return null;
    const placement = placementOf(entry);
    if (placement.kind === 'variant') return variantTitle(placement.variantKey);
    if (placement.kind === 'ambiguous') return `${placement.variantKeys.length} варианта`;
    return null;
  }


  // Группировка по scope: ближайший уровень (Комплект) вверху, дальние — ниже. Пустые группы скрыты.
  const SCOPE_ORDER: CatalogScope[] = ['Set', 'Section', 'Construction', 'System'];
  const groups = SCOPE_ORDER
    .map(s => ({ scope: s, entries: filtered.filter(e => e.scope === s) }))
    .filter(g => g.entries.length > 0);

  const searching = search.trim().length > 0;

  const hints = new Map(filtered.map(e => [e.id, variantHint(e)] as const));

  /**
   * Пустое состояние объясняет ПРИЧИНУ. Раньше текст был один на три случая и в самом частом врал:
   * записи в каталоге есть, просто других типов, — а совет «Добавьте записи в каталог общих данных»
   * отправлял человека заводить дубль того, что уже заведено.
   */
  // Тип самого union'а в список входит: запись такого типа заводит «Вынести в общие данные» (#663),
  // и фильтр её пропускает. Не назови мы его — подсказка перечисляла бы всё, кроме единственного
  // типа, который человек уже, возможно, и создал.
  const acceptedTitles = unionMode
    ? [compositeType!.name,
       ...variantFields.map(f => allDocTypes.find(t => t.id === f.typeId)?.name).filter(Boolean) as string[]]
    : (compositeType ? [compositeType.name] : []);
  const emptyState = searching
    ? { title: `По запросу «${search.trim()}» ничего не найдено.`, hint: 'Измените запрос или очистите поиск.' }
    : catalogEntries.length > 0
      ? {
          title: 'Подходящих записей нет.',
          hint: acceptedTitles.length > 0
            ? `Сюда годятся записи типов: ${acceptedTitles.slice(0, 3).join(', ')}`
              + (acceptedTitles.length > 3 ? ` и ещё ${acceptedTitles.length - 3}` : '')
              + `. На доступных уровнях записей: ${catalogEntries.length}, но другого типа.`
            : `На доступных уровнях записей: ${catalogEntries.length}, но ни одна не подходит по типу.`,
        }
      : { title: 'Нет объектов доступных для ссылки.', hint: 'Добавьте записи в каталог общих данных.' };
  const firstScope = groups[0]?.scope; // ближайшая НЕпустая группа — раскрыта по умолчанию
  // Ручные переопределения сворачивания (действуют, когда поиск пуст). При поиске все группы с
  // совпадениями раскрыты (иначе матч спрятался бы за свёрнутой группой).
  const [collapseOverride, setCollapseOverride] = useState<Partial<Record<CatalogScope, boolean>>>({});
  const isExpanded = (scope: CatalogScope) =>
    searching ? true : (collapseOverride[scope] ?? scope === firstScope);
  const toggleGroup = (scope: CatalogScope) =>
    setCollapseOverride(o => ({ ...o, [scope]: !isExpanded(scope) }));

  /**
   * Поля других документов комплекта того же типа — второй источник значения.
   *
   * <p>Поиск фильтрует и этот список тоже (issue #750). Пока раздел был мёртв, это было незаметно;
   * с живым разделом набранный запрос сузил бы только каталог, а первым в клавиатурном порядке
   * остался бы документ, запросу НЕ отвечающий, — и Enter выбрал бы не то, что человек искал.</p>
   *
   * <p><code>filled</code> — заполнено ли поле-источник. Пустое не прячем: связать сейчас, а
   * заполнить потом — нормальный порядок работы. Но и молчать нельзя: неразвёрнутая ссылка доходит
   * до проверки как ошибка «целевая запись не найдена или удалена», хотя документ на месте и не
   * заполнено всего одно поле (см. issue про ветку <code>case "document"</code> резолвера).</p>
   */
  const docSources = compositeType && setId
    ? otherInstances.flatMap(inst => {
        const dt = allDocTypes.find(t => t.id === inst.documentTypeId);
        if (!dt) return [];
        const fields = resolveEffectiveFields(dt, allDocTypes).filter(
          f => f.type === 'complex' && f.typeId === compositeType.id,
        );
        return fields
          .map(f => ({ inst, dt, field: f, label: sourceLabel(inst, dt, f),
                       filled: !isBlank(inst.requisites?.[f.key]) }))
          .filter(o => o.label.toLowerCase().includes(search.trim().toLowerCase()));
      })
    : [];

  // Плоский список навигируемых опций (issue #107 F5): видимые (в раскрытых группах) записи
  // каталога + источники-документы — в порядке отображения. Стрелки/Enter ходят по ним.
  type RpOption =
    | { type: 'catalog'; entry: CommonDataEntry }
    | { type: 'doc'; inst: DocumentInstance; dt: DocumentType; field: SchemaField;
        label: string; filled: boolean };
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

  const refOf = (entry: CommonDataEntry): FieldRef => ({
    $ref: 'catalog',
    entryId: entry.id,
    displayName: entry.displayName,
    scope: entry.scope,
  });

  function selectCatalog(entry: CommonDataEntry) {
    if (!unionMode) {
      onSelect(refOf(entry));
      onOpenChange(false);
      return;
    }
    const placement = placementOf(entry);
    // Ничья — единственный случай, когда диалог не закрывается: спрашиваем вариант вторым шагом.
    if (placement.kind === 'ambiguous') { setAskVariantFor(entry); return; }
    onSelect(refOf(entry), placement.kind === 'variant' ? placement.variantKey : undefined);
    onOpenChange(false);
  }

  function selectVariant(entry: CommonDataEntry, variantKey: string) {
    onSelect(refOf(entry), variantKey);
    setAskVariantFor(null);
    onOpenChange(false);
  }

  function selectDocument(inst: DocumentInstance, dt: DocumentType, field: SchemaField) {
    onSelect({
      $ref: 'document',
      instanceId: inst.id,
      fieldKey: field.key,
      displayName: sourceLabel(inst, dt, field),
    });
    onOpenChange(false);
  }

  // Второй шаг: тип не назвал единственного варианта — спрашиваем. Отдельным экраном той же
  // модалки, а не отдельным диалогом: выбор ещё не сделан, и «назад» обязано возвращать к списку.
  if (askVariantFor) {
    const placement = placementOf(askVariantFor);
    const keys = placement.kind === 'ambiguous' ? placement.variantKeys : [];
    return (
      <Modal open={open} onOpenChange={onOpenChange} title="В какой вариант поместить?">
        <div className="space-y-4">
          <p className="text-sm text-fg2">
            <span className="font-medium">{askVariantFor.displayName}</span> подходит нескольким
            вариантам одинаково: они объявлены на один и тот же тип, и по типу выбрать нельзя.
          </p>
          <VariantPicker layout="list" options={keys.map(k => ({ key: k, label: variantTitle(k), filled: false }))}
            active="" onSelect={k => selectVariant(askVariantFor, k)} />
          <button type="button" onClick={() => setAskVariantFor(null)}
            className="text-xs text-fg3 hover:text-fg1 transition-colors">← Назад к списку</button>
        </div>
      </Modal>
    );
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
                              {hints.get(entry.id) && (
                                <span className={`text-[11px] shrink-0 ml-2 truncate max-w-[45%] ${on ? 'text-on-tonal' : 'text-fg4'}`}>
                                  {hints.get(entry.id)}
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
          </div>
        )}

        {docSources.length > 0 && (
          <div>
            <p className="text-xs font-medium text-fg3 uppercase tracking-wide mb-2">
              Из других документов комплекта
            </p>
            <div className="space-y-1">
              {docSources.map(({ inst, dt, field, label, filled }) => {
                const gi = indexByKey.get(`d:${inst.id}-${field.key}`) ?? -1;
                const on = gi === active;
                return (
                  <button key={`${inst.id}-${field.key}`} type="button" role="option" aria-selected={on} id={`rp-opt-${gi}`}
                    onMouseEnter={() => setActive(gi)} onClick={() => selectDocument(inst, dt, field)}
                    className={`w-full flex items-center gap-3 px-3 py-2 text-sm text-left rounded-md transition-colors ${
                      on ? 'bg-tonal text-on-tonal' : 'hover:bg-brand-subtle'}`}>
                    <FileText size={14} className={`shrink-0 ${on ? 'text-on-tonal' : 'text-fg4'}`} />
                    <span className={`flex-1 font-medium truncate ${on ? 'text-on-tonal' : 'text-fg1'}`}>
                      {label}
                    </span>
                    {!filled && (
                      <span className={`text-[11px] shrink-0 ${on ? 'text-on-tonal' : 'text-warning'}`}
                        title="Поле-источник пока не заполнено — ссылка останется неразвёрнутой, пока его не заполнят">
                        не заполнено
                      </span>
                    )}
                  </button>
                );
              })}
            </div>
          </div>
        )}

        {filtered.length === 0 && docSources.length === 0 && (
          <p className="text-sm text-fg4 text-center py-4">
            {emptyState.title}
            <br />
            <span className="text-xs">{emptyState.hint}</span>
            {/* Шов между двумя входами (issue #751): этот диалог отвечает на вопрос «дай строку
                ЦЕЛИКОМ», а документ в ОДИН вариант строки выбирают внутри неё. Пока диалоги не
                сведены, единственное место, где об этом можно сказать, — здесь.
                Формулировка правится вместе с #750: раздел документов комплекта у массива больше
                не пуст всегда — он показывает поля того же типа, что вся строка. */}
            {unionMode && (
              <>
                <br />
                <span className="text-xs">
                  Документы комплекта предлагаются здесь только целой строкой — полем того же типа.
                  Чтобы поставить документ в ОДИН вариант: «Добавить строку» → вариант →
                  «Выбрать документ…».
                </span>
              </>
            )}
          </p>
        )}
      </div>
    </Modal>
  );
}
