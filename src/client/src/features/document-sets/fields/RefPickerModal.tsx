import { useState, useEffect } from 'react';
import { FileText, Files } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { useCommonDataForScope } from '@/shared/api/commonData';
import type {
  CommonDataEntry, DocumentInstance, DocumentType, FieldRef, CatalogScope,
} from '@/shared/api/types';
import {
  resolveEffectiveFields, isSubtypeOf, isUnionType, placeInUnion,
  type SchemaField, type UnionPlacement, type UnionSource,
} from '@/shared/api/schema';
import { STATUS_COLORS, STATUS_LABELS } from './constants';
import { VariantPicker } from './VariantPicker';
import { useScopeGroups } from './catalogGroups';
import { ScopeGroupList } from './ScopeGroupList';

/**
 * Подпись источника-поля: «имя документа → поле», с откатом на имя типа.
 *
 * <p>Раньше подпись строилась от типа — «АОСР → Представитель подрядчика». В комплекте АОСР бывает
 * несколько (у живого их два), и такие строки становились неотличимы: какой именно документ
 * протягивается, из списка не видно, а после выбора не видно и в самой ссылке — та же строка едет
 * в её displayName. Имя экземпляра там, где оно есть, ровно как у документа целиком.</p>
 */
function sourceLabel(inst: DocumentInstance, dt: DocumentType, f: SchemaField): string {
  return `${inst.name || dt.name} → ${f.title}`;
}

/** Подпись документа целиком — та же, что кладёт «Выбрать документ…» (InstancePickerModal). */
function instanceLabel(inst: DocumentInstance, dt: DocumentType): string {
  return inst.name ? `${inst.name} (${dt.name})` : dt.name;
}

/** Значение поля-источника отсутствует: null/undefined, пустая строка, пустой объект или массив. */
function isBlank(v: unknown): boolean {
  if (v == null || v === '') return true;
  if (Array.isArray(v)) return v.length === 0;
  if (typeof v === 'object') return Object.keys(v as object).length === 0;
  return false;
}

/**
 * Кандидат в строку: чем подписан, какой ссылкой ляжет и в какой вариант union'а (issue #751).
 *
 * <p>Три источника — запись каталога, документ комплекта целиком, поле другого документа — до сих пор
 * были раскиданы по двум диалогам с разными множествами кандидатов. Здесь они приведены к одному
 * виду, поэтому и выбор, и второй шаг «в какой вариант положить» у них общие: разойтись, как
 * разошлись диалоги, им больше нечем.</p>
 */
type Candidate = { label: string; ref: FieldRef; placement: UnionPlacement };

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
   * предлагает записи типов-ВАРИАНТОВ, а не только самого составного типа, и документы комплекта
   * целиком (issue #751).
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
  // варианту. Куда ляжет кандидат, считаем здесь же и несём до onSelect — второй раз тот же вопрос
  // задавать негде: в обработчике выбора уже нет ни типов, ни цепочки наследования под рукой.
  const unionMode = unionAware && !!compositeType && isUnionType(compositeType, allDocTypes);
  /**
   * Размещения считаем по ТИПУ и заранее, а не по кандидату и лениво.
   *
   * <p>Решение зависит только от типа источника, а типов на порядки меньше, чем записей и
   * документов: у живого комплекта тридцать записей каталога дают четыре-пять типов. Каждый вызов
   * <code>placeInUnion</code> заново разрешает схему union'а и идёт вверх по цепочке наследования,
   * поэтому считать его на кандидата — значит платить за это при каждом нажатии клавиши в поиске.</p>
   *
   * <p>Именно готовыми таблицами, а не кэшем-по-требованию: спрашивают их теперь и колбэки,
   * отданные наружу (подсказки строк каталога), а такой кэш заполнялся бы уже ПОСЛЕ рендера — и
   * следующий рендер видел бы другое содержимое, чем предыдущий.</p>
   *
   * <p>Таблицы раздельные по виду источника: у одного и того же типа исходы для значения и для
   * документа разные (см. <code>UnionSource</code>).</p>
   */
  const placementsFor = (typeIds: string[], source: UnionSource) =>
    new Map<string, UnionPlacement>(
      unionMode
        ? [...new Set(typeIds)].map(id => [id, placeInUnion(id, compositeType!, allDocTypes, source)] as const)
        : [],
    );
  const valuePlacements = placementsFor(catalogEntries.map(e => e.compositeTypeId), 'value');
  const docPlacements = placementsFor(otherInstances.map(i => i.documentTypeId), 'document');
  /** Тип поля-источника среди записей каталога может и не встретиться — тогда считаем на месте. */
  const placementOf = (typeId: string, source: UnionSource = 'value'): UnionPlacement => {
    const table = source === 'document' ? docPlacements : valuePlacements;
    return table.get(typeId) ?? placeInUnion(typeId, compositeType!, allDocTypes, source);
  };

  const filtered = catalogEntries.filter(e => {
    if (compositeType) {
      const fits = unionMode
        ? placementOf(e.compositeTypeId).kind !== 'none'
        : isSubtypeOf(e.compositeTypeId, compositeType.id, allDocTypes);
      if (!fits) return false;
    }
    return e.displayName.toLowerCase().includes(search.toLowerCase());
  });

  // Кандидат, для которого тип не назвал единственного варианта: показываем его вторым шагом со
  // списком вариантов вместо того, чтобы прятать. Прятать нельзя — ничья означает, что два
  // варианта объявлены на один тип (иначе при одиночном наследовании дистанции не совпали бы),
  // то есть это осмысленная схема, а не порча данных, и исправить её из пикера человек не может.
  const [askVariantFor, setAskVariantFor] = useState<Candidate | null>(null);
  useEffect(() => { if (!open) setAskVariantFor(null); }, [open]);

  /** Подписи вариантов union'а — для метки на кандидате и для второго шага. */
  const variantFields = unionMode
    ? resolveEffectiveFields(compositeType!, allDocTypes).filter(f => f.type === 'complex' || f.type === 'doc-ref')
    : [];
  const variantTitle = (key: string) => variantFields.find(f => f.key === key)?.title ?? key;

  // Метка «куда ляжет» — только когда принимающих слотов больше одного: при единственном варианте
  // она повторяла бы заголовок поля и была бы шумом.
  function variantHint(placement: UnionPlacement): string | null {
    if (!unionMode || variantFields.length < 2) return null;
    if (placement.kind === 'variant') return variantTitle(placement.variantKey);
    if (placement.kind === 'ambiguous') return `${placement.variantKeys.length} варианта`;
    return null;
  }

  const searching = search.trim().length > 0;
  const query = search.trim().toLowerCase();
  const { groups, isExpanded, toggle, visible } = useScopeGroups(filtered, searching);

  const refOfEntry = (entry: CommonDataEntry): FieldRef => ({
    $ref: 'catalog',
    entryId: entry.id,
    displayName: entry.displayName,
    scope: entry.scope,
  });

  /**
   * Документы комплекта целиком — источник, которого у этого диалога не было (issue #751).
   *
   * <p>Строка union-массива вида <code>{Вариант: {"$ref":"instance"}}</code> совершенно законна, и для
   * реестров это основной случай: строки реестра суть документы комплекта. Раньше «Из каталога» их
   * не предлагал вовсе, и документ ставили обходом — создать строку, открыть её, выбрать вариант,
   * «Выбрать документ…». Ограничение было свойством КНОПКИ, а не модели.</p>
   *
   * <p>Принимают документ только <code>doc-ref</code>-варианты (см. <code>UnionSource</code>), поэтому
   * там, где вариантов такого вида нет — например у «Кабельной линии», набранной одними
   * <code>complex</code>, — раздел просто пуст, а не заполнен неподходящими документами.</p>
   */
  const instanceCandidates = unionMode
    ? otherInstances.flatMap(inst => {
        const dt = allDocTypes.find(t => t.id === inst.documentTypeId);
        if (!dt) return [];
        const placement = placementOf(inst.documentTypeId, 'document');
        if (placement.kind === 'none') return [];
        const label = instanceLabel(inst, dt);
        // Поиск ведём и по имени документа, и по имени типа — имя у экземпляра необязательно.
        if (query && !label.toLowerCase().includes(query) && !dt.name.toLowerCase().includes(query)) return [];
        return [{ inst, dt, label, placement }];
      })
    : [];

  /**
   * Поля других документов комплекта — второй источник значения.
   *
   * <p>Поиск фильтрует и этот список тоже (issue #750). Пока раздел был мёртв, это было незаметно;
   * с живым разделом набранный запрос сузил бы только каталог, а первым в клавиатурном порядке
   * остался бы документ, запросу НЕ отвечающий, — и Enter выбрал бы не то, что человек искал.</p>
   *
   * <p>В union-режиме годность поля считает <code>placeInUnion</code>, а не равенство типов: поле
   * типа ВАРИАНТА — такой же законный источник строки, как запись каталога того же типа, и прежнее
   * ограничение «ровно тип всего union'а» отсекало его без причины (issue #751).</p>
   *
   * <p>Берём только <code>complex</code>-поля: <code>doc-ref</code>-поле само хранит ссылку, и
   * <code>$ref:'document'</code> на него дал бы ссылку на ссылку — известный дефект #762, который
   * незачем делать частым.</p>
   *
   * <p><code>filled</code> — заполнено ли поле-источник. Пустое не прячем: связать сейчас, а
   * заполнить потом — нормальный порядок работы. Но и молчать нельзя: неразвёрнутая ссылка доходит
   * до проверки как ошибка «целевая запись не найдена или удалена», хотя документ на месте и не
   * заполнено всего одно поле.</p>
   */
  const fieldCandidates = compositeType && setId
    ? otherInstances.flatMap(inst => {
        const dt = allDocTypes.find(t => t.id === inst.documentTypeId);
        if (!dt) return [];
        const fields = resolveEffectiveFields(dt, allDocTypes).filter(f => {
          if (f.type !== 'complex' || !f.typeId) return false;
          return unionMode ? placementOf(f.typeId).kind !== 'none' : f.typeId === compositeType.id;
        });
        return fields
          .map(f => ({
            inst, dt, field: f, label: sourceLabel(inst, dt, f),
            filled: !isBlank(inst.requisites?.[f.key]),
            placement: unionMode ? placementOf(f.typeId!) : ({ kind: 'self' } as UnionPlacement),
          }))
          .filter(o => o.label.toLowerCase().includes(query));
      })
    : [];

  // Плоский список навигируемых опций (issue #107 F5): видимые (в раскрытых группах) записи
  // каталога + документы целиком + поля-источники — в порядке отображения. Стрелки/Enter ходят по ним.
  type RpOption =
    | { type: 'catalog'; entry: CommonDataEntry }
    | { type: 'instance'; inst: DocumentInstance; dt: DocumentType; label: string; placement: UnionPlacement }
    | { type: 'field'; inst: DocumentInstance; dt: DocumentType; field: SchemaField;
        label: string; filled: boolean; placement: UnionPlacement };
  const options: RpOption[] = [
    ...visible.map(entry => ({ type: 'catalog' as const, entry })),
    ...instanceCandidates.map(d => ({ type: 'instance' as const, ...d })),
    ...fieldCandidates.map(d => ({ type: 'field' as const, ...d })),
  ];
  const [active, setActive] = useState(0);
  useEffect(() => { setActive(0); }, [search]);
  const optKey = (o: RpOption) =>
    o.type === 'catalog' ? `c:${o.entry.id}`
      : o.type === 'instance' ? `i:${o.inst.id}`
        : `d:${o.inst.id}-${o.field.key}`;
  const indexByKey = new Map(options.map((o, i) => [optKey(o), i]));
  const optionId = (key: string) => {
    const i = indexByKey.get(key);
    return i == null ? undefined : `rp-opt-${i}`;
  };
  const isOn = (key: string) => indexByKey.get(key) === active;

  function toCandidate(o: RpOption): Candidate {
    if (o.type === 'catalog') {
      return {
        label: o.entry.displayName,
        ref: refOfEntry(o.entry),
        placement: unionMode ? placementOf(o.entry.compositeTypeId) : { kind: 'self' },
      };
    }
    if (o.type === 'instance') {
      return {
        label: o.label,
        ref: { $ref: 'instance', instanceId: o.inst.id, displayName: o.label },
        placement: o.placement,
      };
    }
    return {
      label: o.label,
      ref: { $ref: 'document', instanceId: o.inst.id, fieldKey: o.field.key, displayName: o.label },
      placement: o.placement,
    };
  }

  /** Единственная дверь к выбору: ничья спрашивает вариант, всё остальное закрывает диалог. */
  function choose(o: RpOption) {
    const c = toCandidate(o);
    if (c.placement.kind === 'ambiguous') { setAskVariantFor(c); return; }
    onSelect(c.ref, c.placement.kind === 'variant' ? c.placement.variantKey : undefined);
    onOpenChange(false);
  }

  function onKey(e: React.KeyboardEvent) {
    if (e.key === 'ArrowDown') { e.preventDefault(); setActive(a => Math.min(a + 1, options.length - 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setActive(a => Math.max(a - 1, 0)); }
    else if (e.key === 'Enter') { e.preventDefault(); const o = options[active]; if (o) choose(o); }
  }

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

  // Второй шаг: тип не назвал единственного варианта — спрашиваем. Отдельным экраном той же
  // модалки, а не отдельным диалогом: выбор ещё не сделан, и «назад» обязано возвращать к списку.
  if (askVariantFor) {
    const keys = askVariantFor.placement.kind === 'ambiguous' ? askVariantFor.placement.variantKeys : [];
    return (
      <Modal open={open} onOpenChange={onOpenChange} title="В какой вариант поместить?">
        <div className="space-y-4">
          <p className="text-sm text-fg2">
            <span className="font-medium">{askVariantFor.label}</span> подходит нескольким
            вариантам одинаково: они объявлены на один и тот же тип, и по типу выбрать нельзя.
          </p>
          <VariantPicker layout="list" options={keys.map(k => ({ key: k, label: variantTitle(k), filled: false }))}
            active="" onSelect={k => { onSelect(askVariantFor.ref, k); setAskVariantFor(null); onOpenChange(false); }} />
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
            <ScopeGroupList
              groups={groups} isExpanded={isExpanded}
              // Сворачивание меняет состав навигируемого списка: не сбрось мы активную позицию,
              // стрелки продолжили бы с номера, который теперь указывает на чужую запись.
              toggle={s => { toggle(s); setActive(0); }}
              isActive={e => isOn(`c:${e.id}`)}
              onHover={e => { const i = indexByKey.get(`c:${e.id}`); if (i != null) setActive(i); }}
              onSelect={e => { const i = indexByKey.get(`c:${e.id}`); if (i != null) choose(options[i]); }}
              optionIdOf={e => optionId(`c:${e.id}`)}
              hintOf={e => variantHint(unionMode ? placementOf(e.compositeTypeId) : { kind: 'self' })}
            />
          </div>
        )}

        {instanceCandidates.length > 0 && (
          <div>
            <p className="text-xs font-medium text-fg3 uppercase tracking-wide mb-2">
              Документы комплекта
            </p>
            <div className="space-y-1 max-h-48 overflow-y-auto">
              {instanceCandidates.map(({ inst, label, placement }) => {
                const key = `i:${inst.id}`;
                const on = isOn(key);
                const hint = variantHint(placement);
                return (
                  <button key={inst.id} type="button" role="option" aria-selected={on} id={optionId(key)}
                    onMouseEnter={() => { const i = indexByKey.get(key); if (i != null) setActive(i); }}
                    onClick={() => { const i = indexByKey.get(key); if (i != null) choose(options[i]); }}
                    className={`w-full flex items-center gap-3 px-3 py-2 text-sm text-left rounded-md transition-colors ${
                      on ? 'bg-tonal text-on-tonal' : 'hover:bg-brand-subtle'}`}>
                    <Files size={14} className={`shrink-0 ${on ? 'text-on-tonal' : 'text-fg4'}`} />
                    <span className={`flex-1 font-medium truncate ${on ? 'text-on-tonal' : 'text-fg1'}`}>
                      {label}
                    </span>
                    {hint && (
                      <span className={`text-[11px] shrink-0 truncate max-w-[35%] ${on ? 'text-on-tonal' : 'text-fg4'}`}>
                        {hint}
                      </span>
                    )}
                    <span className={`text-xs px-1.5 py-0.5 rounded font-medium shrink-0 ${STATUS_COLORS[inst.status]}`}>
                      {STATUS_LABELS[inst.status]}
                    </span>
                  </button>
                );
              })}
            </div>
          </div>
        )}

        {fieldCandidates.length > 0 && (
          <div>
            <p className="text-xs font-medium text-fg3 uppercase tracking-wide mb-2">
              Поля других документов
            </p>
            <div className="space-y-1 max-h-48 overflow-y-auto">
              {fieldCandidates.map(({ inst, field, label, filled, placement }) => {
                const key = `d:${inst.id}-${field.key}`;
                const on = isOn(key);
                const hint = variantHint(placement);
                return (
                  <button key={key} type="button" role="option" aria-selected={on} id={optionId(key)}
                    onMouseEnter={() => { const i = indexByKey.get(key); if (i != null) setActive(i); }}
                    onClick={() => { const i = indexByKey.get(key); if (i != null) choose(options[i]); }}
                    className={`w-full flex items-center gap-3 px-3 py-2 text-sm text-left rounded-md transition-colors ${
                      on ? 'bg-tonal text-on-tonal' : 'hover:bg-brand-subtle'}`}>
                    <FileText size={14} className={`shrink-0 ${on ? 'text-on-tonal' : 'text-fg4'}`} />
                    <span className={`flex-1 font-medium truncate ${on ? 'text-on-tonal' : 'text-fg1'}`}>
                      {label}
                    </span>
                    {hint && (
                      <span className={`text-[11px] shrink-0 truncate max-w-[35%] ${on ? 'text-on-tonal' : 'text-fg4'}`}>
                        {hint}
                      </span>
                    )}
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

        {options.length === 0 && (
          <p className="text-sm text-fg4 text-center py-4">
            {emptyState.title}
            <br />
            <span className="text-xs">{emptyState.hint}</span>
          </p>
        )}
      </div>
    </Modal>
  );
}
