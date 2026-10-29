import { useState } from 'react';
import { ModalExit } from '@/shared/ui/Modal';
import { WindowStep } from '@/shared/ui/WindowStep';
import { Button } from '@/shared/ui/Button';
import type { CatalogScope, DocumentType, FieldRef } from '@/shared/api/types';
import { resolveObjectsBatch, type ObjectResolveItem, type ObjectResolveResult } from '@/shared/api/objects';
import { FUNCTIONAL_TAG, hasTag } from '@/shared/api/tags';
import { resolveEffectiveFields, type SchemaField } from '@/shared/api/schema';
import { ruPlural } from '@/shared/utils/pluralize';
import { useLocale, resolveLocale, LOCALE_OPTIONS, SYSTEM_LOCALE } from '@/shared/hooks/useLocale';
import { dateMask } from '@/shared/format/dateMask';
import { useListPrimitiveTypes } from '@/shared/api/primitiveTypes';
import { useListEnumTypes } from '@/shared/api/enumTypes';
import { coerceScalar, rejectReason, type CoerceContext } from './pasteCoerce';
import { PasteArchivedNote, type ArchivedCell } from './PasteArchivedNote';
import { useReturnManyFromArchive } from '@/shared/api/commonData';
import { useCan } from '@/shared/api/access';
import { useToast } from '@/shared/ui/Toast';
import { Select, SelectItem } from '@/shared/ui/Select';
// ─── Paste mapping modal ──────────────────────────────────────────────────────

/** Сколько неразобранных ячеек перечислять поимённо: остальные — числом «и ещё N». */
const REJECTS_SHOWN = 8;

/** Ячейка, не разобранная по типу поля: называется человеку вместе со своим значением. */
interface RejectedCell { rowIndex: number; line: number; title: string; raw: string; reason: string }

const norm = (s: string) => s.trim().toLowerCase().replace(/\s+/g, ' ');

/** Авто-сопоставление по заголовкам первой строки. */
function headerMapping(tableFields: SchemaField[], headerRow: string[], count: number): Record<string, number> {
  const m: Record<string, number> = {};
  tableFields.forEach(f => {
    const ci = headerRow.findIndex((cell, i) =>
      i < count && (norm(cell) === norm(f.title) || norm(cell) === norm(f.key)));
    if (ci >= 0) m[f.key] = ci;
  });
  return m;
}

/**
 * Разбор вставленного текста: есть ли строка заголовков и какие колонки каким полям отвечают.
 *
 * <p>Чистая функция на уровне модуля (issue #858), потому что тот же ответ нужен в двух местах:
 * обработчику вставки — чтобы обновить состояние, и инициализатору состояния — чтобы диалог,
 * открытый с уже готовым текстом, сразу открылся на шаге сопоставления. Раньше вторым занимался
 * эффект, и первый рендер успевал показать пустой шаг «вставьте данные».</p>
 */
function detectMapping(tableFields: SchemaField[], text: string): { skipHeader: boolean; fieldCol: Record<string, number> } {
  const rows = text.trim().split('\n').map(r => r.split('\t'));
  const maxCols = rows.length > 0 ? Math.max(...rows.map(r => r.length)) : 0;
  const firstRow = rows[0] ?? [];
  const skipHeader = firstRow.some(cell => {
    const c = norm(cell);
    return !!c && tableFields.some(f => norm(f.title) === c || norm(f.key) === c);
  });
  return { skipHeader, fieldCol: skipHeader ? headerMapping(tableFields, firstRow, maxCols) : {} };
}

interface PasteMappingModalProps {
  open: boolean; onOpenChange: (v: boolean) => void;
  initialText: string;
  tableFields: SchemaField[];
  allDocTypes: DocumentType[];
  /** Scope-контекст владельца — для серверного резолва «строка→объект» (issue #183). */
  scope?: CatalogScope; scopeId?: string | null;
  onApply: (rows: Record<string, unknown>[]) => void;
}

/**
 * Вставка из Excel — шаги окна таблицы, а не окно над ним (issue #1384): «Отмена» возвращает к таблице
 * с её строками. Тело монтируется по открытию (issue #858): каждое открытие начинается с того
 * текста, с которым вставку позвали, и разбирает его до первого рендера — а не эффектом после него.
 */
const MAP_HEAD = 'bg-surface-container-low px-2 py-1.5 text-on-surface-variant font-medium border-b border-outline-variant';
const MAP_CELL = 'px-2 py-1.5 border-b border-outline-variant';

export function PasteMappingModal(props: PasteMappingModalProps) {
  return props.open ? <PasteMappingModalBody {...props} /> : null;
}

function PasteMappingModalBody({
  onOpenChange, initialText, tableFields, allDocTypes, scope, scopeId, onApply,
}: PasteMappingModalProps) {
  const [initial] = useState(() => detectMapping(tableFields, initialText));
  // Числа разбираются по той же региональной настройке, какой они показываются (issue #953):
  // она и решает, что в `12,5` запятая десятичная, а в `1,234` — разделитель тысяч.
  const [locale] = useLocale();
  // Справочники пользовательских типов и списков — теми же хуками, что и таблица: React Query
  // отдаёт их из кэша, лишнего запроса вставка не делает.
  const { data: primitiveTypes = [] } = useListPrimitiveTypes();
  const { data: enumTypes = [] } = useListEnumTypes();
  const coerceCtx: CoerceContext = { locale, primitiveTypes, enumTypes };
  const [step, setStep] = useState<'input' | 'map'>(initialText.trim() ? 'map' : 'input');
  const [rawText, setRawText] = useState(initialText);
  // Три шага, и набранное жалко на каждом: вставленный текст и есть правка. Сопоставление
  // колонок выбирают списками, которых окно не замечает, — поэтому признак явный, один на все шаги.
  const pasted = rawText.trim() !== '';
  const [skipHeader, setSkipHeader] = useState(initial.skipHeader);
  // Сопоставление: ПОЛЕ таблицы → индекс колонки Excel-данных (источник). '' = пропустить.
  const [fieldCol, setFieldCol] = useState<Record<string, number>>(initial.fieldCol);
  // Для составных (complex) полей: 'name' (имя+алиасы) / 'key' (identity-поля) — резолв ссылки на
  // каталог; 'inline' (issue #374) — собрать встроенный объект из значения без поиска.
  const [matchFields, setMatchFields] = useState<Record<string, 'name' | 'key' | 'inline'>>({});

  // Метаданные составного типа поля: identity-поля (тэг identity) + поле для inline-фолбэка.
  function complexMeta(field: SchemaField) {
    const ct = field.typeId ? allDocTypes.find(dt => dt.id === field.typeId) ?? null : null;
    const eff = ct ? resolveEffectiveFields(ct, allDocTypes) : [];
    const identityKeys = eff.filter(fld => hasTag(fld.tags, FUNCTIONAL_TAG.identity)).map(fld => fld.key);
    // Фолбэк-поле для несопоставленного значения: первое identity-поле, иначе первое строковое/числовое.
    const fallbackKey = identityKeys[0] ?? eff.find(fld => fld.type === 'string' || fld.type === 'number')?.key;
    return { identityKeys, fallbackKey };
  }
  const refTo = (hit: ObjectResolveResult): FieldRef =>
    ({ $ref: 'catalog', entryId: hit.entryId, displayName: hit.displayName ?? '', scope: hit.scope }) as FieldRef;
  // Резолв идёт на сервере (issue #183): индикатор + промежуточная сводка перед вставкой.
  const [resolving, setResolving] = useState(false);
  const [pending, setPending] = useState<{
    rows: Record<string, unknown>[]; linked: number; inline: number; rejects: RejectedCell[];
    /** Сколько строк выброшено целиком и какие остались — счёт «пустых ячеек» идёт по оставшимся. */
    dropped: number; kept: Set<number>;
    /** Ячейки, совпавшие с записью в архиве: лежат встроенно, пока их не решат вернуть и связать. */
    archived: ArchivedCell[];
  } | null>(null);
  const [returnArchived, setReturnArchived] = useState(false);
  const unarchive = useReturnManyFromArchive();
  // Свой признак, а не isPending мутации: вставка — это возврат из архива ПЛЮС сама вставка, и
  // пока она идёт, заперты все три кнопки сводки. Иначе повторный клик вставил бы строки дважды, а
  // «Отмена» закрыла бы окно, в которое вставка всё равно придёт (ревью PR #1228).
  const [applying, setApplying] = useState(false);
  const canReturn = useCan().permission('core.catalog.edit');
  const toast = useToast();

  function mapByHeader(headerRow: string[], count: number): Record<string, number> {
    return headerMapping(tableFields, headerRow, count);
  }

  function initMapping(text: string) {
    const { skipHeader: isHeader, fieldCol: cols } = detectMapping(tableFields, text);
    setSkipHeader(isHeader);
    setFieldCol(cols);
    setMatchFields({});
    setPending(null);
    setReturnArchived(false);
  }

  const allRows = rawText.trim() ? rawText.trim().split('\n').map(r => r.split('\t')) : [];
  const maxCols = allRows.length > 0 ? Math.max(...allRows.map(r => r.length)) : 0;
  const dataRows = skipHeader ? allRows.slice(1) : allRows;
  const importCount = dataRows.filter(r => r.some(c => c.trim())).length;

  function colLabel(ci: number): string {
    if (skipHeader) return (allRows[0]?.[ci] ?? '').trim() || `Кол. ${ci + 1}`;
    const preview = dataRows.slice(0, 3).map(r => r[ci] ?? '').filter(Boolean).join(', ');
    return preview ? `Кол. ${ci + 1} — ${preview}` : `Кол. ${ci + 1}`;
  }

  // Изменения сопоставления инвалидируют промежуточную сводку.
  function clearPending() { if (pending) setPending(null); }

  // Резолв строк на сервере → сборка строк. Complex-поля сопоставляются «по наименованию» (Name)
  // или «по ключу» (identity-поля, OR: значение матчит любое identity-поле). Нет совпадения →
  // inline-данные (сырой текст в identity/строковое под-поле), чтобы НЕ терять ввод (не пусто).
  async function stage() {
    // Номер строки считается по ВСТАВЛЕННОМУ тексту — со строкой заголовков и пропущенными
    // пустыми: сводка зовёт «исправьте источник», значит человек пойдёт искать эту строку у себя.
    const lineOffset = skipHeader ? 2 : 1;
    const dataOnly: { cells: string[]; line: number }[] = [];
    dataRows.forEach((r, i) => { if (r.some(c => c.trim())) dataOnly.push({ cells: r, line: i + lineOffset }); });
    const rows: Record<string, unknown>[] = dataOnly.map(() => ({}));
    const flat: ObjectResolveItem[] = [];
    // На complex-ячейку — диапазон [start, start+count) запросов в flat; фолбэк — куда класть inline.
    const cells: { row: number; fieldKey: string; raw: string; start: number; count: number; fallbackKey?: string }[] = [];
    // Ячейки, которые не легли в тип поля: остаются пустыми и НАЗЫВАЮТСЯ в сводке (issue #1064).
    const rejects: RejectedCell[] = [];

    dataOnly.forEach(({ cells: r, line }, ri) => {
      Object.entries(fieldCol).forEach(([fieldKey, ci]) => {
        const field = tableFields.find(f => f.key === fieldKey);
        if (!field) return;
        const raw = (r[ci] ?? '').trim();
        if (!raw) return;
        if (field.type === 'complex') {
          const strat = matchFields[fieldKey];
          if (!strat || !field.typeId) return;
          const { identityKeys, fallbackKey } = complexMeta(field);
          // Встроенный режим (issue #374): собрать объект из значения сразу, без поиска в каталоге.
          if (strat === 'inline') {
            rows[ri][fieldKey] = fallbackKey ? { [fallbackKey]: raw } : {};
            return;
          }
          const start = flat.length;
          if (strat === 'name') {
            flat.push({ typeId: field.typeId, strategy: 'Name', value: raw });
          } else {
            if (identityKeys.length === 0) return; // «по ключу» без identity-полей — нечем матчить
            identityKeys.forEach(k => flat.push({ typeId: field.typeId!, strategy: 'Field', value: raw, fieldKey: k }));
          }
          cells.push({ row: ri, fieldKey, raw, start, count: flat.length - start, fallbackKey });
        } else {
          const v = coerceScalar(field, raw, coerceCtx);
          if (v !== null) rows[ri][fieldKey] = v;
          else rejects.push({ rowIndex: ri, line, title: field.title, raw, reason: rejectReason(field, coerceCtx) });
        }
      });
    });

    const inlineFallback = (c: typeof cells[number]) => { rows[c.row][c.fieldKey] = c.fallbackKey ? { [c.fallbackKey]: c.raw } : {}; };

    let linked = 0, inline = 0;
    const archived: ArchivedCell[] = [];
    setResolving(true);
    try {
      const results = await resolveObjectsBatch(scope, scopeId, flat);
      cells.forEach(c => {
        // Действующее совпадение по ЛЮБОМУ из ключей важнее архивного по первому: иначе запись в
        // архиве заслонила бы действующую только потому, что её ключ спросили раньше.
        const hits = results.slice(c.start, c.start + c.count).filter((r): r is ObjectResolveResult => !!r);
        const hit = hits.find(h => !h.archived) ?? hits[0];
        if (hit && !hit.archived) {
          rows[c.row][c.fieldKey] = refTo(hit);
          linked++;
          return;
        }
        // Архивную запись молча не подставляем (issue #1185): ячейка ложится встроенно, а связать
        // её можно осознанно — переключателем в сводке. Считается она ОТДЕЛЬНО от встроенных:
        // у тех пояснение «совпадение не найдено», а здесь оно найдено.
        inlineFallback(c);
        if (hit) archived.push({ row: rows[c.row], fieldKey: c.fieldKey, hit });
        else inline++;
      });
    } catch {
      // Ошибка резолва не должна терять ввод — вставляем всё как inline-данные.
      cells.forEach(inlineFallback);
      inline = cells.length; linked = 0; archived.length = 0;
    } finally {
      setResolving(false);
    }

    // Строка, у которой ВСЕ сопоставленные ячейки ушли в отказ, не вставляется вовсе: пустая
    // строка в таблице — не данные, а мусор, который человеку ещё и удалять руками. Отказы по
    // таким строкам НЕ обещают «пустых ячеек» — обещать их в несуществующей строке нечестно.
    const keptRows = new Set<number>();
    rows.forEach((r, i) => { if (Object.keys(r).length > 0) keptRows.add(i); });
    const filled = rows.filter((_, i) => keptRows.has(i));
    const dropped = rows.length - filled.length;

    // Сводка перед вставкой нужна, если есть что назвать: несопоставленные ссылки ИЛИ ячейки,
    // не разобранные по типу. Молча уехать может только полностью разобранная вставка.
    if (inline === 0 && rejects.length === 0 && archived.length === 0) { onApply(filled); onOpenChange(false); }
    else { setReturnArchived(false); setPending({ rows: filled, linked, inline, rejects, dropped, kept: keptRows, archived }); }
  }

  /**
   * Вставка из сводки. Переключатель включён — сначала возвращаем записи из архива, и связываем
   * только те ячейки, чью запись вернуть УДАЛОСЬ: ссылка на запись, оставшуюся в архиве, была бы
   * новой ссылкой на то, что из выбора убрали, а ячейка без неё не теряется — она лежит встроенно.
   */
  async function applyPending() {
    if (!pending || applying) return;
    setApplying(true);
    try {
      if (returnArchived && pending.archived.length > 0) {
        const ids = [...new Set(pending.archived.map(c => c.hit.entryId))];
        const returned = new Set(await unarchive.mutateAsync(ids).catch(() => [] as string[]));
        pending.archived.forEach(c => { if (returned.has(c.hit.entryId)) c.row[c.fieldKey] = refTo(c.hit); });
        const failed = ids.length - returned.size;
        if (returned.size > 0) toast.success(`Возвращено из архива: ${returned.size}.`);
        if (failed > 0) toast.error(`Не удалось вернуть из архива: ${failed}. Эти значения вставлены встроенно.`);
      }
      onApply(pending.rows);
      onOpenChange(false);
    } finally {
      setApplying(false);
    }
  }

  // Отказы делятся надвое: в строках, которые вставятся (ячейка останется пустой), и в строках,
  // которые выброшены целиком — обещать пустую ячейку в невставленной строке нечестно.
  const keptRejects = pending ? pending.rejects.filter(c => pending.kept.has(c.rowIndex)).length : 0;
  const numberRejected = !!pending?.rejects.some(c => c.reason === 'не число');
  // Дату тоже читают в порядке настройки (issue #1461): «01.02.2026» у ru-RU — 1 февраля, у en-US —
  // 2 января. Скопированная из Excel дата в чужом порядке разберётся иначе или откажет — сказать почему.
  const dateRejected = !!pending?.rejects.some(c => c.reason.startsWith('не дата'));
  // Маска и ISO — обозначениями того же языка: «MM/DD/YYYY или YYYY-MM-DD», а не вперемешку с ГГГГ.
  const { text: maskText, labels } = dateMask(resolveLocale(locale));
  const dateHint = `${maskText} или ${labels.y}-${labels.m}-${labels.d}`;

  // Отказ по числу необъясним без имени настройки, по которой его разбирали.
  const localeLabel = locale === SYSTEM_LOCALE
    ? `${LOCALE_OPTIONS.find(o => o.value === SYSTEM_LOCALE)?.label}: ${resolveLocale(locale)}`
    : LOCALE_OPTIONS.find(o => o.value === locale)?.label ?? locale;

  const selectCls = 'w-full min-w-[140px]';

  if (step === 'input') {
    return (
      <WindowStep onBack={() => onOpenChange(false)} title="Вставить из Excel" wide isDirty={pasted}
        footer={
          <div className="flex justify-end gap-3">
            <ModalExit />
            <Button variant="filled" disabled={!rawText.trim()}
              onClick={() => { initMapping(rawText); setStep('map'); }}>
              Далее →
            </Button>
          </div>
        }>
        <div className="space-y-4">
          <p className="text-sm text-on-surface-variant">Скопируйте ячейки в Excel и вставьте сюда (Ctrl+V):</p>
          <textarea autoFocus value={rawText} onChange={e => setRawText(e.target.value)}
            onPaste={e => {
              const text = e.clipboardData.getData('text/plain');
              if (text.trim()) { e.preventDefault(); setRawText(text); initMapping(text); setStep('map'); }
            }}
            placeholder="Вставьте данные из Excel..." rows={7}
            className="w-full border border-outline rounded-sm px-3 py-2 text-xs font-mono focus:outline-none focus-visible:ring-2 focus-visible:ring-primary bg-surface resize-none" />
        </div>
      </WindowStep>
    );
  }

  // step === 'map', промежуточная сводка перед вставкой (есть несопоставленные строки).
  if (pending) {
    return (
      <WindowStep onBack={() => onOpenChange(false)} title="Готово к вставке" wide isDirty={pasted} busy={applying}
        footer={
          <div className="flex justify-between">
            <Button variant="text" disabled={applying} onClick={() => setPending(null)}>← Изменить сопоставление</Button>
            <div className="flex gap-3">
              <ModalExit />
              <Button variant="filled" disabled={pending.rows.length === 0 || applying} loading={applying}
                onClick={() => void applyPending()}>
                Вставить {pending.rows.length} стр.
              </Button>
            </div>
          </div>
        }>
        <div className="space-y-3 text-sm">
          <p className="text-on-surface-variant">Будет вставлено строк: <span className="font-medium text-on-surface">{pending.rows.length}</span>
            {pending.rows.length < importCount && (
              <span className="text-on-surface-variant"> (из {importCount}: остальные не дали ни одного значения)</span>
            )}
          </p>
          {pending.linked + pending.inline > 0 && (
            <ul className="space-y-1.5">
              <li className="text-on-surface-variant">🔗 Связано с каталогом: <span className="font-medium text-on-surface">{pending.linked}</span></li>
              <li className="text-on-surface-variant">
                📝 Встроенные данные (без связи с каталогом): <span className="font-medium text-on-surface">{pending.inline}</span>
              </li>
            </ul>
          )}
          {pending.rejects.length > 0 && (
            <div className="space-y-1.5">
              <p className="text-on-surface-variant">
                ⚠️ Не разобрано: <span className="font-medium text-on-surface">{pending.rejects.length}</span>
                {' '}{ruPlural(pending.rejects.length, 'ячейка', 'ячейки', 'ячеек')}
              </p>
              <ul className="space-y-0.5 max-h-40 overflow-y-auto">
                {pending.rejects.slice(0, REJECTS_SHOWN).map((c, i) => (
                  <li key={i} className="text-xs text-on-surface-variant">
                    строка {c.line}, «{c.title}»: {c.reason}
                    {' — '}<span className="font-mono text-on-surface">{c.raw}</span>
                  </li>
                ))}
              </ul>
              {pending.rejects.length > REJECTS_SHOWN && (
                <p className="text-xs text-on-surface-variant">…и ещё {pending.rejects.length - REJECTS_SHOWN}</p>
              )}
              <p className="text-xs text-on-surface-variant">
                Значение не уложилось в тип поля целиком, и подставлять вместо него догадку нельзя.
                {keptRejects > 0 && ` Таких ячеек во вставляемых строках: ${keptRejects} — они останутся пустыми, заполните вручную либо исправьте источник.`}
                {pending.dropped > 0 && ` Строк, где не разобралось ничего: ${pending.dropped} — они не вставятся вовсе.`}
              </p>
              {numberRejected && (
                <p className="text-xs text-on-surface-variant">
                  Числа разбираются по региональной настройке (<span className="text-on-surface-variant">{localeLabel}</span>):
                  десятичный разделитель — её, остальные знаки могут быть только разделителями тысяч.
                </p>
              )}
              {dateRejected && (
                <p className="text-xs text-on-surface-variant">
                  Даты разбираются в порядке региональной настройки (<span className="text-on-surface-variant">{localeLabel}</span>):{' '}
                  {dateHint}. Дата из таблицы с другим порядком дня и месяца читается по настройке, а не угадывается.
                </p>
              )}
              {(numberRejected || dateRejected) && (
                <p className="text-xs text-on-surface-variant">Настройка — в профиле, раздел «Внешний вид».</p>
              )}
            </div>
          )}
          {pending.inline > 0 && (
            <p className="text-xs text-on-surface-variant">
              Встроенные ячейки — обычные данные в документе (выбран режим «Встроенно» либо совпадение
              в каталоге не найдено). Их можно дозаполнить/связать вручную после вставки.
            </p>
          )}
          {pending.archived.length > 0 && (
            <PasteArchivedNote cells={pending.archived} canReturn={canReturn}
              checked={returnArchived} onChange={setReturnArchived} />
          )}
        </div>
      </WindowStep>
    );
  }

  // step === 'map' — строки = ПОЛЯ таблицы, справа выбираем колонку Excel-источник.
  return (
    <WindowStep onBack={() => onOpenChange(false)} title="Сопоставление столбцов" wide isDirty={pasted} busy={resolving}
      footer={
        <div className="flex justify-between">
          <Button variant="text" onClick={() => setStep('input')}>← Назад</Button>
          <div className="flex gap-3">
            <ModalExit />
            <Button variant="filled" onClick={stage} loading={resolving} disabled={importCount === 0 || resolving}>
              Импортировать {importCount > 0 ? `(${importCount} стр.)` : ''}
            </Button>
          </div>
        </div>
      }>
      <div className="space-y-4">
        <div className="flex items-center gap-3">
          <label className="flex items-center gap-2 text-sm text-on-surface-variant cursor-pointer">
            <input type="checkbox" checked={skipHeader}
              onChange={e => {
                const checked = e.target.checked;
                setSkipHeader(checked);
                setFieldCol(checked ? mapByHeader(allRows[0] ?? [], maxCols) : {});
                setMatchFields({});
                clearPending();
              }}
              className="w-4 h-4 rounded-xs border-outline text-primary" />
            Первая строка — заголовки
          </label>
          <span className="text-xs text-on-surface-variant">Найдено строк данных: {importCount}</span>
        </div>
        {/* Форма, а не таблица данных (#1354): строка — поле, в клетках — выбор. Поэтому сетка
            раскладки, а не таблица: читать здесь нечего, здесь задают. */}
        <div className="overflow-x-auto">
          <div className="grid grid-cols-3 min-w-[480px] text-xs border border-outline-variant rounded-sm overflow-hidden [&>div:last-child>div]:border-b-0">
            <div className={MAP_HEAD}>Поле таблицы</div>
            <div className={MAP_HEAD}>Столбец из Excel</div>
            <div className={MAP_HEAD}>Сопоставить по</div>
            {tableFields.map(f => {
              const ci = fieldCol[f.key];
              const needsMatch = f.type === 'complex';
              const hasIdentity = needsMatch && complexMeta(f).identityKeys.length > 0;
              return (
                <div key={f.key} className="contents">
                  <div className={MAP_CELL}>
                    <span className="text-on-surface font-medium">{f.title}</span>
                    {f.required && <span className="text-error ml-0.5">*</span>}
                  </div>
                  <div className={MAP_CELL}>
                    <Select size="sm" className={selectCls} aria-label={`Колонка для «${f.title}»`}
                      value={ci == null ? '' : String(ci)} emptyItem="— пропустить —"
                      onValueChange={v => {
                        clearPending();
                        setFieldCol(prev => {
                          const n = { ...prev };
                          if (v === '') delete n[f.key]; else n[f.key] = Number(v);
                          return n;
                        });
                      }}>
                      {Array.from({ length: maxCols }, (_, i) => (
                        <SelectItem key={i} value={String(i)}>{colLabel(i)}</SelectItem>
                      ))}
                    </Select>
                  </div>
                  <div className={MAP_CELL}>
                    {needsMatch && ci != null && (
                      <Select size="sm" className={selectCls} aria-label={`Как сопоставлять «${f.title}»`}
                        value={matchFields[f.key] ?? ''} placeholder="— выберите —"
                        onValueChange={m => { clearPending(); setMatchFields(prev => ({ ...prev, [f.key]: m as 'name' | 'key' | 'inline' })); }}>
                        <SelectItem value="name">🔗 Наименование</SelectItem>
                        {hasIdentity && <SelectItem value="key">🔗 Ключ</SelectItem>}
                        <SelectItem value="inline">{'{}'} Встроенно (без поиска)</SelectItem>
                      </Select>
                    )}
                  </div>
                </div>
              );
            })}
          </div>
        </div>
      </div>
    </WindowStep>
  );
}
