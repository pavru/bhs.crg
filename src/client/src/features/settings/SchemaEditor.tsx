/**
 * Редактор схемы полей — самая большая часть страницы типов (458 строк из 1419).
 *
 * Выделено из `DocumentTypesPage.tsx` (issue #1031). `normalizeGroupMembership` приехал вместе с ним
 * и остался приватным: его зовёт только этот редактор, а вынос наружу нарушил бы
 * `react-refresh/only-export-components`.
 */
import { useState, useMemo, useRef } from 'react';
import { apiError } from '@/shared/utils/apiError';
import { SchemaLevelBanner } from './SchemaLevelBanner';
import { Braces, Code, Cpu, HelpCircle, RefreshCw, AlertTriangle } from 'lucide-react';
import { Markdown } from '@/shared/ui/Markdown';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { useUpdateDocumentTypeSchema, useMigrateFieldKey, useIdentityImpact, type IdentityImpact } from '@/shared/api/documentTypes';
import { ruCount, ruPlural } from '@/shared/utils/pluralize';
import { useListPrimitiveTypes } from '@/shared/api/primitiveTypes';
import { useListEnumTypes } from '@/shared/api/enumTypes';
import type { DocumentType } from '@/shared/api/types';
import { chainFieldKeys, parseSchemaFields, resolveEffectiveFields, type SchemaField, type SchemaDefinition, type FieldGroup, type TypstRender } from '@/shared/api/schema';
import { danglingKeyRefs, danglingRefPlaces } from './schemaKeyChecks';
import { TypstRendersEditor } from './TypstRendersEditor';
import { TypstBlocksPanel } from './TypstBlocksCheck';
import { useTypstBlocksCheck, blocksCheckProblemsByFn } from './useTypstBlocksCheck';
import { schemaToJson, validateFields } from './schemaConstants';
import { useTagRegistry, typeTags as typeTagDefs, unknownTagCodes, FUNCTIONAL_TAG } from '@/shared/api/tags';
import { GroupedFieldsEditor } from './GroupedFieldsEditor';
import { JsonPreview } from './FieldBuilder';
import type { FieldRegistries } from './fieldTypeOptions';
import { SectionCard } from './typeEditorShell';
import { useRegisterEditor } from './typeEditorRegistry';
import { useToast } from '@/shared/ui/Toast';
import { InheritedFieldsPanel } from './InheritedFieldsPanel';

/** Единственное членство (issue #197 Фаза C): каждый ключ поля остаётся только в первой группе,
 *  где встречается. Легаси-схемы могли класть поле в несколько групп — нормализуем при загрузке. */
function normalizeGroupMembership(gs: FieldGroup[]): FieldGroup[] {
  const seen = new Set<string>();
  return gs.map(g => ({
    ...g,
    fieldKeys: g.fieldKeys.filter(k => (seen.has(k) ? false : (seen.add(k), true))),
  }));
}

// ─── Schema editor (inline) ────────────────────────────────────────────────────

export function SchemaEditor({ docType, allDocTypes, onSelectType }: {
  docType: DocumentType;
  allDocTypes: DocumentType[];
  onSelectType: (id: string) => void;
}) {
  const { data: primitiveTypes = [] } = useListPrimitiveTypes();
  const { data: enumTypes = [] } = useListEnumTypes();
  const schemaDef = docType.schema as unknown as SchemaDefinition;
  const [fields, setFields] = useState<SchemaField[]>(() => parseSchemaFields(docType.schema));
  // Ключи полей из СОХРАНЁННОЙ схемы (issue #355): их ключи заморожены (переименование не меняет ключ).
  // Новое поле (ключа нет в наборе) — ключ авто-следует за именем. Пересчитывается при сохранении/сбросе.
  const persistedKeys = useMemo(() => new Set(parseSchemaFields(docType.schema).map(f => f.key)), [docType.schema]);
  const [groups, setGroups] = useState<FieldGroup[]>(() => normalizeGroupMembership(schemaDef.groups ?? []));
  const [excludedFields, setExcludedFields] = useState<string[]>(() => schemaDef.excludedFields ?? []);
  const [fieldOverrides, setFieldOverrides] = useState<Record<string, { required?: boolean; defaultValue?: unknown }>>(
    () => schemaDef.fieldOverrides ?? {},
  );
  const [typstRenders, setTypstRenders] = useState<TypstRender[]>(() => schemaDef.typstRenders ?? []);
  const blocksCheck = useTypstBlocksCheck(docType.id, onSelectType);
  const [docTypeTags, setDocTypeTags] = useState<string[]>(() => schemaDef.tags ?? []);
  const [ungroupedOrder, setUngroupedOrder] = useState<string[]>(() => schemaDef.ungroupedOrder ?? []);
  const [help, setHelp] = useState<string>(() => schemaDef.help ?? '');
  const [showHelp, setShowHelp] = useState(false);
  const [helpPreview, setHelpPreview] = useState(false);
  const { data: tagRegistry } = useTagRegistry();
  const applicableTypeTags = typeTagDefs(tagRegistry, docType.kind);
  // Тэги вне реестра этого экземпляра — их не предлагают, но и не прячут (issue #959).
  const unknownTypeTags = unknownTagCodes(docTypeTags, tagRegistry);
  const [showJson, setShowJson] = useState(false);
  const [showTypstRenders, setShowTypstRenders] = useState(typstRenders.length > 0);
  const [showTypeTags, setShowTypeTags] = useState(false);
  const [error, setError] = useState('');
  const [dirty, setDirty] = useState(false);
  const mutation = useUpdateDocumentTypeSchema();
  // Миграция ключа (issue #357): накапливаем переименования ключей сохранённых полей (origKey→newKey),
  // после успешного сохранения схемы предлагаем перенести данные документов старый→новый.
  const renamesRef = useRef<Map<string, string>>(new Map());
  const migrateKey = useMigrateFieldKey(docType.id);
  const schemaToast = useToast();
  const [pendingMigration, setPendingMigration] = useState<{ from: string; to: string }[] | null>(null);
  // Гейт правки полей-идентификаторов (issue #584): держим решение пользователя как промис, потому
  // что спрашивать надо ВНУТРИ save() — до записи схемы, а не после, когда связки уже осиротели.
  const identityImpact = useIdentityImpact();
  const [identityGate, setIdentityGate] = useState<
    { impact: IdentityImpact; decide: (proceed: boolean) => void } | null>(null);

  const compositeTypes = allDocTypes.filter(dt => dt.kind === 'Composite');
  const parentType = docType.parentId ? allDocTypes.find(dt => dt.id === docType.parentId) ?? null : null;
  const parentEffectiveFields = parentType ? resolveEffectiveFields(parentType, allDocTypes) : [];
  const inheritedKeys = new Set(parentEffectiveFields.map(f => f.key));
  // Ссылки на несуществующие поля (issue #639). Сверяем со ВСЕМИ ключами цепочки наследования ДО
  // исключений, а не с эффективными полями родителя: исключение ссылается на поле, которое
  // существует, — в этом его смысл, и по эффективному набору собственное исключение потомка
  // выглядело бы висячим (см. chainFieldKeys).
  const chainKeys = parentType ? chainFieldKeys(parentType, allDocTypes) : [];
  const danglingRefs = danglingKeyRefs(
    { groups, excludedFields, ungroupedOrder, fieldOverrideKeys: Object.keys(fieldOverrides) },
    [...chainKeys, ...fields.map(f => f.key)]);
  const dropDanglingRefs = () => {
    const dead = new Set(danglingRefs.map(r => r.key));
    setGroups(gs => gs.map(g => ({ ...g, fieldKeys: g.fieldKeys.filter(k => !dead.has(k)) })));
    setExcludedFields(ks => ks.filter(k => !dead.has(k)));
    setUngroupedOrder(ks => ks.filter(k => !dead.has(k)));
    setFieldOverrides(o => Object.fromEntries(Object.entries(o).filter(([k]) => !dead.has(k))));
    setDirty(true);
  };
  const effectiveFields = resolveEffectiveFields(docType, allDocTypes);
  const reg: FieldRegistries = { compositeTypes, primitiveTypes, enumTypes, allDocTypes, tagRegistry };
  // Унаследованные поля для группировки — активные (исключённые не показываем в раскладке).
  const activeInheritedFields = parentEffectiveFields.filter(f => !excludedFields.includes(f.key));

  const handleExclude = (key: string) => {
    setExcludedFields(prev => [...prev.filter(k => k !== key), key]);
    setFieldOverrides(prev => { const n = { ...prev }; delete n[key]; return n; });
    setDirty(true);
  };
  const handleInclude = (key: string) => { setExcludedFields(prev => prev.filter(k => k !== key)); setDirty(true); };
  const handleOverrideRequired = (key: string, required: boolean) => {
    setFieldOverrides(prev => ({ ...prev, [key]: { ...prev[key], required } })); setDirty(true);
  };
  const handleOverrideDefaultValue = (key: string, value: unknown) => {
    setFieldOverrides(prev => {
      const cur = prev[key] ?? {};
      if (value === undefined) {
        const { defaultValue: _, ...rest } = cur as { required?: boolean; defaultValue?: unknown };
        return Object.keys(rest).length ? { ...prev, [key]: rest } : { ...prev, [key]: rest };
      }
      return { ...prev, [key]: { ...cur, defaultValue: value } };
    }); setDirty(true);
  };
  const handleResetOverride = (key: string) => {
    setFieldOverrides(prev => { const n = { ...prev }; delete n[key]; return n; }); setDirty(true);
  };

  // Сохранение схемы: бросает при ошибке валидации/мутации — чтобы общий «Сохранить»/гард
  // прерывались, а ошибка показывалась здесь же (issue #197 Фаза C).
  async function save() {
    setError('');
    const fieldError = validateFields(fields);
    if (fieldError) { setError(fieldError); throw new Error(fieldError); }
    const conflict = fields.find(f => inheritedKeys.has(f.key.trim()));
    if (conflict) { const m = `Ключ "${conflict.key}" уже есть в родительском типе`; setError(m); throw new Error(m); }

    // Имена Typst-блоков уникальны В ПРЕДЕЛАХ ТИПА (issue #773): каждый тип — свой модуль со своей
    // областью, и `Адрес.full` рядом с `Подписант.full` совершенно законны. Прежняя проверка на
    // уникальность по всей системе осталась бы прямым запретом на результат миграции — она сама
    // срезает префиксы и порождает такие совпадения, после чего схему было бы не сохранить.
    const definedFnNames = typstRenders.map(r => r.fnName.trim()).filter(Boolean);
    const localDup = definedFnNames.find((n, i) => definedFnNames.indexOf(n) !== i);
    if (localDup) { const m = `Имя функции "${localDup}" задано дважды в этом типе`; setError(m); throw new Error(m); }

    const schemaJson = schemaToJson(fields, excludedFields, fieldOverrides, groups, typstRenders, docTypeTags, ungroupedOrder, help);

    // Правка полей-идентификаторов осиротит связки «материал → документ качества» (issue #584):
    // ключ составной, и добавление поля, снятие тэга или перенумерация меняют ключи ВСЕХ материалов
    // разом. Молча этого делать нельзя — документ выпустился бы без сертификатов при здоровом виде
    // в UI. Отказ сервера считать последствия сохранение не блокирует: предупреждение — не гейт
    // целостности, а помощь, и ронять из-за него правку схемы неправильно.
    try {
      const impact = await identityImpact.mutateAsync({ id: docType.id, schema: schemaJson });
      if (impact.changed && impact.affectedLinks > 0) {
        const proceed = await new Promise<boolean>(decide => setIdentityGate({ impact, decide }));
        if (!proceed) {
          const m = 'Сохранение отменено: правка полей-идентификаторов не подтверждена';
          setError(m); throw new Error(m);
        }
      }
    } catch (err: unknown) {
      if (err instanceof Error && err.message.startsWith('Сохранение отменено')) throw err;
    }

    try {
      await mutation.mutateAsync({ id: docType.id, schema: schemaJson });
      setDirty(false);
      // Проверка сборки блоков после сохранения (issue #309, фаза 2) — не блокирует save.
      if (typstRenders.length > 0) void blocksCheck.run(typstRenders);
      // issue #357: у сохранённого поля сменился ключ (persistedKeys — старые ключи в этом замыкании) →
      // предложить перенос данных документов старый→новый. Схема уже сохранена (ключ переехал в ней).
      const renames = [...renamesRef.current]
        .filter(([from, to]) => from !== to && persistedKeys.has(from) && fields.some(f => f.key.trim() === to));
      renamesRef.current.clear();
      if (renames.length) setPendingMigration(renames.map(([from, to]) => ({ from, to })));
    } catch (err: unknown) {
      // Текст сервера, а не «Request failed with status code 409». С уровнями правки схемы
      // (issue #956) это стало решающим: отказ называет ПОЛЕ и причину — «поле модуля „Табельный
      // номер" удалено или переименовано», — а схема правится десятком изменений сразу, и без
      // имени поля откатывать нечего.
      setError(apiError(err, 'Ошибка сохранения'));
      throw err;
    }
  }
  useRegisterEditor('schema', dirty, save, () => {
    setFields(parseSchemaFields(docType.schema));
    setGroups(normalizeGroupMembership(schemaDef.groups ?? []));
    setExcludedFields(schemaDef.excludedFields ?? []);
    setFieldOverrides(schemaDef.fieldOverrides ?? {});
    setTypstRenders(schemaDef.typstRenders ?? []);
    setDocTypeTags(schemaDef.tags ?? []);
    setUngroupedOrder(schemaDef.ungroupedOrder ?? []);
    setHelp(schemaDef.help ?? '');
    setError(''); setDirty(false);
  });

  return (
    <div className="space-y-4">
      <SchemaLevelBanner level={docType.editLevel} module={docType.module} />
      {parentType && (
        <div>
          <div className="flex items-center gap-2 mb-2">
            <p className="text-xs font-medium text-fg3 uppercase tracking-wide">
              Унаследовано от «{parentType.name}»
            </p>
            {excludedFields.length > 0 && (
              <span className="text-xs text-fg4">(исключено: {excludedFields.length})</span>
            )}
          </div>
          <InheritedFieldsPanel
            parentEffectiveFields={parentEffectiveFields}
            excludedFields={excludedFields}
            fieldOverrides={fieldOverrides}
            compositeTypes={compositeTypes}
            enumTypes={enumTypes}
            onExclude={handleExclude}
            onInclude={handleInclude}
            onOverrideRequired={handleOverrideRequired}
            onOverrideDefaultValue={handleOverrideDefaultValue}
            onResetOverride={handleResetOverride}
          />
        </div>
      )}

      {/* Висячие ссылки (issue #639): ключ в раскладке групп или в исключениях, которому не
          соответствует ни своё поле, ни унаследованное. В интерфейсе такой ключ не виден нигде —
          в раскладке рисуются поля, а не имена, — поэтому «ДатаДокумнета» и пережил все правки
          схемы. Чистим по кнопке и обычным «Сохранить»: молча править чужую схему нельзя. */}
      {danglingRefs.length > 0 && (
        <div className="flex items-start gap-2 rounded-md border border-warning/50 bg-warning/10 px-3 py-2">
          <AlertTriangle size={14} className="text-warning shrink-0 mt-0.5" />
          <div className="text-xs text-fg2 space-y-1 flex-1 min-w-0">
            <p>В схеме есть ссылки на поля, которых нет ни среди своих, ни среди унаследованных —
              скорее всего опечатка в ключе:</p>
            <ul className="space-y-0.5">
              {danglingRefs.map(r => (
                <li key={r.key}>
                  <span className="font-mono">{r.key}</span>
                  <span className="text-fg4"> — {danglingRefPlaces(r)}</span>
                </li>
              ))}
            </ul>
          </div>
          <button type="button" onClick={dropDanglingRefs}
            className="text-xs px-2 py-1 rounded border border-warning/60 text-fg2 hover:bg-warning/20 shrink-0">
            Убрать ссылки
          </button>
        </div>
      )}

      <div>
        <div className="flex items-center justify-between mb-2">
          <p className="text-xs font-medium text-fg3 uppercase tracking-wide">
            {parentType ? 'Поля и группировка' : 'Поля'}
          </p>
          {(fields.length > 0 || parentEffectiveFields.length > 0) && (
            <button type="button" onClick={() => setShowJson(v => !v)}
              className={`flex items-center gap-1.5 text-xs px-2 py-1 rounded ${
                showJson ? 'bg-fg1 text-muted' : 'text-fg3 hover:text-fg1 hover:bg-muted'
              }`}>
              <Braces size={12} /> JSON
            </button>
          )}
        </div>
        {showJson && <JsonPreview fields={fields} groups={groups} excludedFields={excludedFields} fieldOverrides={fieldOverrides} />}
        {/* Редактор ПРЯЧЕМ, а не размонтируем (issue #527): в нём живёт состояние карточек полей —
            база сравнения ключа, снятый замок, раскрытая карточка. Заглянув в JSON посреди
            переименования, пользователь возвращался к полю, которое снова считается новым: замок
            снят молча, предупреждение о дрейфе данных пропало, перенос данных уже не предложится. */}
        <div className={showJson ? 'hidden' : undefined}>
            <GroupedFieldsEditor
              fields={fields}
              onFieldsChange={f => { setFields(f); setDirty(true); }}
              groups={groups}
              onGroupsChange={g => { setGroups(g); setDirty(true); }}
              ungroupedOrder={ungroupedOrder}
              onUngroupedOrderChange={o => { setUngroupedOrder(o); setDirty(true); }}
              parentEffectiveFields={activeInheritedFields}
              disabledKeys={inheritedKeys}
              persistedKeys={persistedKeys}
              onKeyRename={(from, to) => renamesRef.current.set(from, to)}
              reg={reg}
            />
        </div>
      </div>

      {/* Тэги типа, которых в реестре этого экземпляра нет, — тэги выключенного модуля (#959).
          Та же строка, что у поля, и по той же причине: тип с невидимой меткой выглядит обычным,
          а ведёт себя иначе, стоит модуль включить обратно. */}
      {!showJson && unknownTypeTags.length > 0 && (
        <div className="flex flex-wrap items-center gap-1.5 px-1 pt-1">
          <span className="text-xs text-fg4">Тэги модуля:</span>
          {unknownTypeTags.map(code => (
            <span key={code}
              title="Тэг модуля, выключенного на этом экземпляре. Пометка остаётся в схеме и заработает, когда модуль включат."
              className="rounded-full border border-dashed border-stroke px-2 py-0.5 text-[11px] text-fg4">
              {code}
            </span>
          ))}
        </div>
      )}

      {!showJson && applicableTypeTags.length > 0 && (
        <SectionCard icon={<Cpu size={15} />} title="Функциональные тэги типа"
          count={docTypeTags.length} countClass="text-purple-600"
          open={showTypeTags} onToggle={() => setShowTypeTags(v => !v)}>
          <div className="flex flex-wrap gap-1.5 pt-2">
            {applicableTypeTags.map(t => {
              const on = docTypeTags.includes(t.code);
              // Ограничение носителей (issue #258): тэг занят другими типами сверх лимита и текущий тип
              // его не несёт → дизейбл + тултип с занятыми типами.
              const max = t.restriction?.maxBearers ?? null;
              const otherBearers = max == null ? [] : allDocTypes.filter(dt => dt.id !== docType.id
                && (((dt.schema as { tags?: string[] }).tags) ?? []).includes(t.code));
              const blocked = max != null && !on && otherBearers.length >= max;
              return (
                <button
                  key={t.code}
                  type="button"
                  disabled={blocked}
                  title={blocked
                    ? `Тэг уже назначен: ${otherBearers.map(b => `«${b.name}»`).join(', ')}. Допустимо не более ${max}.`
                    : t.description}
                  onClick={() => { setDocTypeTags(prev => on ? prev.filter(c => c !== t.code) : [...prev, t.code]); setDirty(true); }}
                  className={`px-2.5 py-1 rounded-full text-xs border transition-colors ${
                    blocked ? 'border-stroke text-fg4/50 opacity-60 cursor-not-allowed'
                      : on ? 'bg-purple-500/15 border-purple-400 text-purple-700'
                        : 'border-stroke text-fg4 hover:border-stroke-strong hover:text-fg2'
                  }`}
                >
                  {t.label}
                </button>
              );
            })}
          </div>
          {(() => {
            // issue #258: если тип назначен профилем уровня — read-only заметка «где редактируется + ключ».
            const p = [
              { code: FUNCTIONAL_TAG.profileConstruction, level: 'Стройка', key: 'стройка' },
              { code: FUNCTIONAL_TAG.profileSection, level: 'Раздел', key: 'раздел' },
              { code: FUNCTIONAL_TAG.profileSet, level: 'Комплект', key: 'комплект' },
            ].find(x => docTypeTags.includes(x.code));
            return p ? (
              <p className="mt-2 text-xs text-fg3">
                Используется как <span className="text-brand-hover font-medium">профиль уровня «{p.level}»</span>:
                его объект редактируется в «Общие данные» уровня, поля доступны в шаблоне как{' '}
                <code className="font-mono bg-muted text-fg1 px-1 rounded">data.уровень.{p.key}.*</code>.
              </p>
            ) : null;
          })()}
        </SectionCard>
      )}

      {!showJson && (
        <SectionCard icon={<HelpCircle size={15} />} title="Справка для пользователя"
          count={help.trim() ? 1 : 0} countClass="text-brand"
          open={showHelp} onToggle={() => setShowHelp(v => !v)}>
          <div className="pt-2 space-y-2">
            <p className="text-xs text-fg4">
              Показывается при редактировании документа этого типа (напр. что подтягивается из профиля
              уровня). Markdown: <code className="font-mono">**жирный**</code>, <code className="font-mono">*курсив*</code>, списки, <code className="font-mono">[ссылки](url)</code>.
            </p>
            <div className="flex items-center gap-3 text-xs">
              <button type="button" onClick={() => setHelpPreview(false)}
                className={!helpPreview ? 'text-brand font-medium' : 'text-fg4 hover:text-fg2'}>Текст</button>
              <button type="button" onClick={() => setHelpPreview(true)}
                className={helpPreview ? 'text-brand font-medium' : 'text-fg4 hover:text-fg2'}>Предпросмотр</button>
            </div>
            {helpPreview ? (
              help.trim()
                ? <div className="rounded-md border border-stroke bg-surface p-3"><Markdown>{help}</Markdown></div>
                : <p className="text-xs text-fg4 italic px-1">Пусто — введите текст на вкладке «Текст».</p>
            ) : (
              <textarea value={help} onChange={e => { setHelp(e.target.value); setDirty(true); }} rows={5}
                placeholder="Напр.: Проект, адрес и заказчик подтягиваются из профиля стройки — заполнять здесь не нужно."
                className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-brand" />
            )}
          </div>
        </SectionCard>
      )}

      {!showJson && (docType.kind === 'Composite' || docType.kind === 'Document') && (
        <SectionCard icon={<Code size={15} />} title="Typst-блоки (варианты отображения)"
          count={typstRenders.length} countClass="text-purple-600"
          open={showTypstRenders} onToggle={() => setShowTypstRenders(v => !v)}>
          <div className="pt-2 space-y-3">
            <div className="flex items-center justify-between">
              <p className="text-xs text-fg4">Функции отображения для Typst-шаблонов.</p>
              <Button variant="text" size="sm" icon={<RefreshCw size={13} className={blocksCheck.checking ? 'animate-spin' : ''} />}
                disabled={blocksCheck.checking} onClick={() => void blocksCheck.run(typstRenders)}>
                Проверить блоки
              </Button>
            </div>
            {blocksCheck.problems && (
              <TypstBlocksPanel problems={blocksCheck.problems} currentTypeId={docType.id} onSelectType={onSelectType} />
            )}
            <TypstRendersEditor
              typeCode={docType.code}
              renders={typstRenders}
              onChange={r => { setTypstRenders(r); setDirty(true); }}
              onBlockCommitted={r => void blocksCheck.run(r)}
              problemsByFn={blocksCheckProblemsByFn(blocksCheck.problems, docType.id)}
              fields={effectiveFields}
              allDocTypes={allDocTypes}
            />
          </div>
        </SectionCard>
      )}

      {!showJson && error && <p className="text-xs text-danger pt-1">{error}</p>}

      {/* Гейт правки полей-идентификаторов (issue #584): показываем, во что превратится ключ и
          сколько связок перестанут находиться. Отмена диалога = отказ, поэтому решение отдаём в
          decide и в onOpenChange тоже. */}
      <ConfirmDialog
        open={!!identityGate}
        onOpenChange={o => { if (!o) { identityGate?.decide(false); setIdentityGate(null); } }}
        title="Изменение полей-идентификаторов осиротит связки"
        errorTitle="Изменение полей-идентификаторов осиротит связки"
        description={identityGate && (
          <div className="space-y-2">
            <p>
              Ключ материала склеивается из всех полей с тэгом «Идентификатор». После сохранения он
              станет другим у ВСЕХ материалов, и{' '}
              <b>{ruCount(identityGate.impact.affectedLinks, 'связка', 'связки', 'связок')}</b>{' '}
              «материал → документ качества»{' '}
              {ruPlural(identityGate.impact.affectedLinks, 'перестанет', 'перестанут', 'перестанут')}{' '}
              находиться. Сертификаты просто не попадут в документ — ошибки при этом не будет.
            </p>
            <div className="text-xs font-mono text-fg3 space-y-0.5">
              <p>было: {identityGate.impact.before.join(' | ') || '—'}</p>
              <p>станет: {identityGate.impact.after.join(' | ') || '—'}</p>
            </div>
            <p className="text-xs text-fg4">
              Связки придётся завести заново — на вкладке «Документы качества» в документе.
            </p>
          </div>
        )}
        confirmLabel="Сохранить схему"
        requireCheckbox="Понимаю, что связки материалов перестанут находиться"
        onConfirm={() => { identityGate?.decide(true); setIdentityGate(null); }}
      />

      {/* Предложение миграции данных при переименовании ключа сохранённого поля (issue #357). */}
      <ConfirmDialog
        open={!!pendingMigration}
        onOpenChange={o => { if (!o) setPendingMigration(null); }}
        title="Перенести данные документов на новый ключ?"
        description={pendingMigration && (
          <div className="space-y-1">
            <p>Ключ(и) поля изменены. Перенести значения существующих документов этого типа со старого ключа на новый?</p>
            <ul className="text-xs font-mono text-fg3">
              {pendingMigration.map(r => <li key={r.from}>{r.from} → {r.to}</li>)}
            </ul>
            {/* issue #737: ключ держат не только реквизиты — привязки наборов ссылаются на него
                своим целевым полем и ключами маппинга. Переносим вместе, иначе привязка осиротеет
                и перестанет заполнять поле молча. */}
            <p className="text-xs text-fg4">
              Вместе с данными переедут привязки наборов данных и шаблоны привязок, нацеленные на этот ключ.
            </p>
            <p className="text-xs text-fg4">Без переноса старые значения останутся под прежним ключом (осиротеют) — их потом покажет «Аудит».</p>
          </div>
        )}
        confirmLabel="Перенести данные"
        onConfirm={async () => {
          const renames = pendingMigration ?? [];
          setPendingMigration(null);
          let docs = 0, bindings = 0, templates = 0;
          for (const r of renames) {
            try {
              const res = await migrateKey.mutateAsync({ oldKey: r.from, newKey: r.to });
              docs += res.migrated; bindings += res.bindings; templates += res.templates;
            }
            catch { schemaToast.error(`Не удалось перенести «${r.from}»`); }
          }
          // Привязки называем только когда они были: в обычном переименовании их нет, и нулевой
          // счётчик в тосте — шум.
          const extra = [
            bindings > 0 ? `привязок: ${bindings}` : null,
            templates > 0 ? `шаблонов: ${templates}` : null,
          ].filter(Boolean).join(', ');
          schemaToast.success(`Перенесено документов: ${docs}${extra ? `; ${extra}` : ''}`);
        }}
      />
    </div>
  );
}
