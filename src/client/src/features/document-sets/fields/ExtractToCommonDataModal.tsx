import { useState, useEffect, useMemo } from 'react';
import { AlertTriangle, Link2, X } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { Select, SelectItem } from '@/shared/ui/Select';
import { TextField } from '@/shared/ui/TextField';
import { useToast } from '@/shared/ui/Toast';
import type { CatalogScope, DocumentType, FieldRef, PrimitiveTypeDef } from '@/shared/api/types';
import { SCOPE_LABELS } from '@/shared/api/types';
import { identityFieldKeys, resolveEffectiveFields } from '@/shared/api/schema';
import { useCreateCommonDataEntry, useCommonDataForScope } from '@/shared/api/commonData';
import { useGetDocumentSet } from '@/shared/api/documentSets';
import { useListPrimitiveTypes } from '@/shared/api/primitiveTypes';
import { useListEnumTypes } from '@/shared/api/enumTypes';
import { resolveObjectsBatch, type ObjectResolveResult } from '@/shared/api/objects';
import { collectConstraintViolations, describeViolationPath } from './collectConstraintViolations';
import { objectSummary } from './objectSummary';
import {
  suggestEntryName, scalarFieldsFor, findBlockingRefs, maxAllowedScope, offeredScopes,
} from './extractToCommonData';

/** Найденный дубликат: по составному ключу — сильное совпадение, по имени — тёзка. */
interface Duplicate {
  match: ObjectResolveResult;
  strong: boolean;
}

/**
 * Вынос уже заполненного inline-объекта в общие данные (issue #663).
 *
 * Создаёт запись каталога из значения составного поля и подставляет на её место ссылку. Ради этого
 * человек прежде уходил из документа, набирал то же самое второй раз руками и возвращался выбирать
 * созданное — а два «одинаковых» объекта, набранных дважды, расходятся пробелом или сокращением и
 * дальше расходятся в PDF.
 *
 * Порядок операций тут единственно возможный: сначала СОЗДАЁМ запись, потом кладём ссылку в
 * реквизиты, а документ сохраняет пользователь обычной кнопкой. Обратный порядок дал бы ссылку на
 * несуществующую запись (issue #332). Плата — запись может остаться «осиротевшей», если документ не
 * сохранят: это валидный переиспользуемый объект, видимый в каталоге и штатно удаляемый, поэтому
 * исход приемлемый, а тост о несохранённом документе про него и напоминает.
 */
export function ExtractToCommonDataModal({
  open, onOpenChange, values, compositeType, allDocTypes, setId, scope, scopeId, onExtracted,
}: {
  open: boolean;
  onOpenChange: (o: boolean) => void;
  /** Значение составного поля — то, что выносим. */
  values: Record<string, unknown>;
  compositeType: DocumentType;
  allDocTypes: DocumentType[];
  /** Комплект документа — из него берётся цепочка уровней. Нет (форма каталога) — цепочка короче. */
  setId?: string;
  /** Уровень владельца: подсказывает уровень по умолчанию там, где комплекта нет. */
  scope?: CatalogScope;
  scopeId?: string | null;
  /** Ссылка на созданную (или найденную) запись — заменяет собой inline-значение. */
  onExtracted: (ref: FieldRef) => void;
}) {
  const toast = useToast();
  const create = useCreateCommonDataEntry();
  const { data: primitiveTypes = EMPTY_PRIMITIVES } = useListPrimitiveTypes();
  const { data: enumTypes = EMPTY_ENUMS } = useListEnumTypes();

  // Цепочка уровней комплекта (issue #587) — та же, что у экрана связок: раздел и стройка нужны,
  // чтобы положить запись выше комплекта.
  const { data: set } = useGetDocumentSet(setId);
  const sectionId = set?.sectionId ?? null;
  const constructionId = set?.constructionId ?? null;

  const subFields = useMemo(
    () => resolveEffectiveFields(compositeType, allDocTypes), [compositeType, allDocTypes]);
  const typeDefs = useMemo(() => ({ primitiveTypes, enumTypes }), [primitiveTypes, enumTypes]);

  // Вложенные ссылки на каталог ограничивают уровень: запись комплекта, попавшая в запись «Системы»,
  // развернётся в чужом комплекте (EntityResolver грузит её по id без scope-фильтра).
  const blocking = useMemo(() => findBlockingRefs(values), [values]);
  const nestedScopeLookup = useNestedScopeLookup(scope, scopeId, setId, open);
  const allowedMax = useMemo(
    () => maxAllowedScope(values, nestedScopeLookup), [values, nestedScopeLookup]);

  /**
   * Идентификатор контейнера для уровня. «Система» живёт без него, остальным он обязателен — уровень
   * без известного id не предлагаем: запись было бы некуда положить.
   *
   * Цепочка есть там, где есть комплект (редактор документа). В форме общих данных её нет, зато
   * известен СВОЙ уровень — он и добавляется, поэтому там выбор короче: свой уровень и «Система».
   */
  const idFor = useMemo(() => (s: CatalogScope): string | null => {
    if (s === 'System') return null;
    const own = scope === s ? scopeId ?? null : null;
    if (s === 'Set') return setId ?? own;
    if (s === 'Section') return sectionId ?? own;
    return constructionId ?? own;
  }, [scope, scopeId, setId, sectionId, constructionId]);

  const offered = useMemo(() => offeredScopes(allowedMax, idFor), [allowedMax, idFor]);

  // Начальные значения считаем ОДИН РАЗ, на монтировании: окно монтируется на каждое открытие
  // (вызывающий рендерит его только открытым), поэтому «сбрасывать при open» нечего.
  //
  // Эффектом это делать нельзя, и дело не в правиле линтера: значение, список подполей и цепочка
  // уровней пересчитываются при каждом рендере родителя, так что эффект срабатывал бы посреди
  // набора и затирал бы уже введённое имя. Тот же класс ловушки, что нестабильный дефолт пропа
  // (issue #305) — только тут она не роняет рендер, а тихо теряет ввод.
  const [name, setName] = useState(
    () => suggestEntryName(values, subFields, identityFieldKeys(compositeType, allDocTypes), typeDefs));
  const [aliases, setAliases] = useState<string[]>([]);
  const [aliasDraft, setAliasDraft] = useState('');
  // Уровень по умолчанию — самый УЗКИЙ доступный, а не самый удобный (прецедент #587): узкая ошибка
  // обратима — записи не окажется там, где её ищут, и это видно сразу; широкая тиха — чужой объект
  // подставится в чужой документ, и не заметит никто.
  const [target, setTarget] = useState<CatalogScope>(() => offered[0] ?? allowedMax);
  const [duplicate, setDuplicate] = useState<Duplicate | null>(null);
  const [checking, setChecking] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  /**
   * Уровень, на котором действительно работаем. Выбор пользователя сверяем со списком предложенных
   * на КАЖДОМ рендере, а не доверяем состоянию: список считается из цепочки уровней, а она доезжает
   * асинхронно — выбранное на старте значение может оказаться вне разрешённого, и записать его
   * значило бы положить объект шире, чем позволяют вложенные ссылки. Именно тихая широкая ошибка.
   */
  const effectiveTarget = offered.includes(target) ? target : offered[0];
  const targetScopeId = effectiveTarget ? idFor(effectiveTarget) : null;
  // Класть некуда: ни один уровень до разрешённого включительно не имеет известного идентификатора.
  const noPlace = offered.length === 0;

  // Ограничения примитивов — тем же сборщиком, что и обе соседние формы: серверной проверки нет
  // вовсе, а запись отсюда подмешивается в ЧУЖИЕ документы.
  const violations = useMemo(
    () => collectConstraintViolations(values, subFields, allDocTypes, primitiveTypes),
    [values, subFields, allDocTypes, primitiveTypes]);

  // Поиск дубликата: имя и составной ключ, оба в выбранном уровне. Порядок identity-полей клиенту
  // знать не нужно — сервер берёт их сам по typeId.
  useEffect(() => {
    if (!open || blocking.length > 0 || !effectiveTarget) return;
    const trimmed = name.trim();
    let cancelled = false;
    const timer = setTimeout(async () => {
      setChecking(true);
      try {
        const [byKey, byName] = await resolveObjectsBatch(effectiveTarget, targetScopeId, [
          { typeId: compositeType.id, strategy: 'IdentityKey', fields: scalarFieldsFor(values) },
          { typeId: compositeType.id, strategy: 'Name', value: trimmed },
        ]);
        if (cancelled) return;
        setDuplicate(byKey ? { match: byKey, strong: true }
          : byName ? { match: byName, strong: false } : null);
      } catch {
        // Отказ поиска не запрещает создание (как в PasteMappingModal): дубликат — предупреждение,
        // а не условие. Молча оставляем баннер пустым.
        if (!cancelled) setDuplicate(null);
      } finally {
        if (!cancelled) setChecking(false);
      }
    }, 350);
    return () => { cancelled = true; clearTimeout(timer); };
  }, [open, name, effectiveTarget, targetScopeId, values, compositeType.id, blocking.length]);

  function addAlias() {
    const t = aliasDraft.trim();
    if (t && !aliases.some(a => a.toLowerCase() === t.toLowerCase())) setAliases(prev => [...prev, t]);
    setAliasDraft('');
  }

  function linkExisting() {
    if (!duplicate) return;
    onExtracted({
      $ref: 'catalog', entryId: duplicate.match.entryId,
      displayName: duplicate.match.displayName ?? '', scope: duplicate.match.scope,
    });
    onOpenChange(false);
    toast.success('Поле связано с существующей записью. Новая не создавалась.');
  }

  async function submit() {
    const trimmed = name.trim();
    if (!trimmed) { setError('Укажите наименование записи.'); return; }
    // Уровень обязан быть СРЕДИ ПРЕДЛОЖЕННЫХ, а не просто иметь идентификатор: проверка «есть id»
    // пропускала запись шире разрешённого, когда список уровней пуст и выбор подставился сам.
    if (!effectiveTarget || !offered.includes(effectiveTarget)) {
      setError('Уровень для записи не определён — выберите его в списке.');
      return;
    }
    if (effectiveTarget !== 'System' && !targetScopeId) {
      setError('Уровень ещё не готов — комплект загружается. Повторите через мгновение.');
      return;
    }
    setBusy(true); setError('');
    try {
      const entry = await create.mutateAsync({
        displayName: trimmed,
        compositeTypeId: compositeType.id,
        data: JSON.stringify(values),
        scope: effectiveTarget,
        scopeId: effectiveTarget === 'System' ? null : targetScopeId,
        aliases: aliases.length > 0 ? aliases : undefined,
      });
      onExtracted({
        $ref: 'catalog', entryId: entry.id, displayName: entry.displayName, scope: effectiveTarget,
      });
      onOpenChange(false);
      toast.success('Запись создана и подставлена ссылкой. Не забудьте сохранить документ.');
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  }

  const violationList = Object.entries(violations);
  const blocked = blocking.length > 0 || violationList.length > 0 || noPlace;

  return (
    <Modal open={open} onOpenChange={onOpenChange} wide
      title={`Вынести в общие данные — ${compositeType.name}`}
      footer={
        <div className="flex justify-end gap-2">
          <Button variant="text" onClick={() => onOpenChange(false)}>Отмена</Button>
          <Button variant="filled" onClick={submit} loading={busy}
            disabled={busy || blocked || !name.trim()}>
            {duplicate ? 'Всё равно создать новую' : 'Создать запись'}
          </Button>
        </div>
      }>
      <div className="px-6 py-4 space-y-4">
        {blocking.length > 0 && (
          <Note tone="danger" icon={<AlertTriangle size={14} />}
            title="Внутри есть ссылки, которые вне своего комплекта не разворачиваются">
            <p className="mb-1">
              Ссылки на документ и его поля резолвятся только в том комплекте, где документ живёт.
              В переиспользуемой записи такая ссылка уйдёт в шаблон как есть. Уберите их или
              заполните значения вручную — вырезать их молча значило бы потерять данные.
            </p>
            <ul className="list-disc pl-4 space-y-0.5">
              {blocking.map(b => (
                <li key={b.path}>
                  <span className="font-mono text-xs">{b.path || '(объект целиком)'}</span> — {b.displayName}
                </li>
              ))}
            </ul>
          </Note>
        )}

        {violationList.length > 0 && (
          <Note tone="danger" icon={<AlertTriangle size={14} />}
            title="Значения не соответствуют ограничениям типов">
            <ul className="list-disc pl-4 space-y-0.5">
              {violationList.map(([path, message]) => (
                <li key={path}>{describeViolationPath(path, subFields, allDocTypes)}: {message}</li>
              ))}
            </ul>
          </Note>
        )}

        <div>
          <label className="block text-sm font-medium text-fg2 mb-1">Тип</label>
          <div className="h-10 flex items-center rounded-md border border-stroke bg-base px-3 text-sm text-fg2">
            {compositeType.name}
          </div>
          <p className="text-xs text-fg4 mt-1">Задан полем — выбирать нечего.</p>
        </div>

        <TextField label="Наименование" value={name} onChange={e => setName(e.target.value)} required autoFocus />

        <div>
          <label className="block text-sm font-medium text-fg2 mb-1">Уровень</label>
          {noPlace ? (
            /* Пустой Select был бы тупиком: список без пунктов, баннер про ограничение и работающая
               кнопка, кладущая запись шире разрешённого. Говорим прямо. */
            <Note tone="danger" icon={<AlertTriangle size={14} />} title="Положить запись некуда">
              Внутри есть ссылка на запись уровня «{SCOPE_LABELS[allowedMax]}», а этот уровень отсюда
              недоступен: {setId ? 'комплект ещё загружается' : 'форма не знает нужного контейнера'}.
              Вынести объект можно из редактора документа того комплекта.
            </Note>
          ) : (
            <>
              <Select value={effectiveTarget} onValueChange={v => setTarget(v as CatalogScope)}
                aria-label="Уровень записи" className="w-full">
                {offered.map(s => <SelectItem key={s} value={s}>{SCOPE_LABELS[s]}</SelectItem>)}
              </Select>
              {allowedMax !== 'System' && (
                <p className="text-xs text-fg4 mt-1">
                  Шире «{SCOPE_LABELS[allowedMax]}» нельзя: внутри есть ссылка на запись этого уровня —
                  из более широкой записи она развернулась бы в чужом комплекте.
                </p>
              )}
            </>
          )}
        </div>

        <div>
          <label className="block text-sm font-medium text-fg2 mb-1">
            Псевдонимы <span className="text-xs text-fg4 font-normal">(для поиска при связывании с источниками)</span>
          </label>
          {aliases.length > 0 && (
            <div className="flex flex-wrap gap-1.5 mb-1.5">
              {aliases.map(a => (
                <span key={a} className="inline-flex items-center gap-1 text-xs bg-muted text-fg2 rounded-2xl pl-2.5 pr-1 py-0.5 max-w-full">
                  <span className="min-w-0 break-words">{a}</span>
                  <button type="button" onClick={() => setAliases(prev => prev.filter(x => x !== a))}
                    className="text-fg4 hover:text-danger transition-colors shrink-0" title="Удалить">
                    <X size={11} />
                  </button>
                </span>
              ))}
            </div>
          )}
          <input value={aliasDraft} onChange={e => setAliasDraft(e.target.value)}
            onKeyDown={e => { if (e.key === 'Enter' || e.key === ',') { e.preventDefault(); addAlias(); } }}
            onBlur={addAlias}
            placeholder="Добавить псевдоним и Enter..."
            className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface" />
        </div>

        {duplicate && (
          <Note tone="warning" icon={<AlertTriangle size={14} />}
            title={duplicate.strong
              ? 'Такая запись уже есть — совпали поля идентичности'
              : 'Есть запись с таким же наименованием'}>
            <p className="mb-2">
              «{duplicate.match.displayName}» на уровне «{SCOPE_LABELS[duplicate.match.scope]}».
              {duplicate.strong
                ? ' Скорее всего это тот же объект.'
                : ' Возможно, это просто тёзка — одноимённые организации встречаются.'}
            </p>
            <p className="text-xs text-fg4 mb-2">
              Поиск идёт в выбранном уровне и выше: записи более узкого уровня отсюда не видны.
            </p>
            <Button variant="tonal" size="sm" icon={<Link2 size={13} />} onClick={linkExisting}>
              Связать с существующей
            </Button>
          </Note>
        )}

        <div className="rounded-lg border border-stroke bg-base/40 px-3 py-2">
          <div className="text-xs text-fg3 mb-1">
            Переносится{checking ? ' · проверяем дубликаты…' : ''}
          </div>
          <div className="text-sm text-fg2 break-words">{objectSummary(values, subFields, typeDefs)}</div>
          <div className="text-xs text-fg4 mt-1">
            Полей: {Object.keys(values ?? {}).length}
            {nestedRefCount(values) > 0 && `, из них ссылок на каталог: ${nestedRefCount(values)}`}
          </div>
        </div>

        {error && <p className="text-sm text-danger">{error}</p>}
      </div>
    </Modal>
  );
}

// ─── Вспомогательное ──────────────────────────────────────────────────────────

/** Модульные пустышки: инлайновый `= []` при загрузке — новый массив на каждый рендер. */
const EMPTY_PRIMITIVES: PrimitiveTypeDef[] = [];
const EMPTY_ENUMS: never[] = [];

function Note({ tone, icon, title, children }: {
  tone: 'danger' | 'warning'; icon: React.ReactNode; title: string; children: React.ReactNode;
}) {
  const cls = tone === 'danger'
    ? 'border-danger/40 bg-danger-subtle text-danger'
    : 'border-warning/40 bg-warning-subtle text-warning';
  return (
    <div className={`rounded-lg border px-3 py-2 text-sm ${cls}`}>
      <div className="flex items-center gap-1.5 font-medium mb-1">{icon}{title}</div>
      <div className="text-fg2">{children}</div>
    </div>
  );
}

/** Сколько внутри ссылок на каталог — их переносим как есть, и об этом стоит сказать вслух. */
function nestedRefCount(value: unknown): number {
  if (value == null || typeof value !== 'object') return 0;
  const v = value as Record<string, unknown>;
  if (v.$ref === 'catalog') return 1;
  if (Array.isArray(value)) return value.reduce<number>((n, item) => n + nestedRefCount(item), 0);
  return Object.values(v).reduce<number>((n, item) => n + nestedRefCount(item), 0);
}

/**
 * Уровень ссылки, у которой его не записали: такие кладёт серверный резолвер привязок
 * (`DataSetResolver`), да и ссылки, заведённые до появления поля, его не несут.
 *
 * Ищем по ВИДИМОЙ ЦЕПОЧКЕ владельца и БЕЗ фильтра по типу — оба условия существенны. `for-scope`
 * разрешает родителей только вниз от переданного уровня, поэтому запрос с «Системой» вернул бы одни
 * системные записи; а фильтр по типу отсекал бы всё, потому что вложенная ссылка указывает на запись
 * типа ПОДПОЛЯ, а не того составного, который выносим. С обеими ошибками карта оставалась пустой,
 * уровень всегда доопределялся как «Комплект», и выбор молча схлопывался в один пункт.
 *
 * Не нашли — вызывающий консервативно считает «Комплект».
 */
function useNestedScopeLookup(
  scope: CatalogScope | undefined, scopeId: string | null | undefined, setId: string | undefined,
  enabled: boolean,
) {
  const { data: entries } = useCommonDataForScope({
    scope: setId ? 'Set' : scope ?? 'System',
    scopeId: setId ?? scopeId ?? null,
    enabled,
  });
  return useMemo(() => {
    const byId = new Map((entries ?? []).map(e => [e.id, e.scope]));
    return (entryId: string) => byId.get(entryId);
  }, [entries]);
}

function errorText(e: unknown): string {
  const detail = (e as { response?: { data?: { detail?: string; title?: string } } })?.response?.data;
  return detail?.detail ?? detail?.title ?? 'Не удалось создать запись.';
}
