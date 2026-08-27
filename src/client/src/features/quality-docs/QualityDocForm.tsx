import { useState, useMemo, useRef } from 'react';
import { Loader2, ShieldCheck, Upload, Eye } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { apiError } from '@/shared/utils/apiError';
import { TypePickerField } from '@/shared/ui/TypePickerField';
import type { PickType } from '@/shared/ui/TypePicker';
import { TextField } from '@/shared/ui/TextField';
import {
  useCreateQualityDoc, useUpdateQualityDoc, useSetQualityDocScan, recognizeDocument,
  type QualityDocument,
} from '@/shared/api/qualityDocs';
import { buildRecognitionFields, codesFromLabels } from './recognitionFields';
import { uploadAttachment, openAttachmentInNewTab } from '@/shared/api/attachments';
import type { DocumentType, CatalogScope } from '@/shared/api/types';
import { resolveEffectiveFields, typeHasTag, findTaggedFieldPath, type SchemaField } from '@/shared/api/schema';
import { FUNCTIONAL_TAG } from '@/shared/api/tags';
import { useListPrimitiveTypes } from '@/shared/api/primitiveTypes';
import { useListEnumTypes } from '@/shared/api/enumTypes';
import {
  PrimitiveInput, ComplexFieldGroup, ArrayFieldEditor, DocRefCatalogPickerField, ImageField, FileField,
  validateConstraint, collectConstraintViolations, describeViolationPath,
} from '@/features/document-sets/fields';
import { useUploadsInFlight } from '@/shared/ui/uploadsInFlight';
import { RECOGNIZED_HINT, recognizedFieldKeys, applyRecognized } from './recognizedMark';

/** Поля, занимающие обе колонки сетки — тот же набор, что в редакторе реквизитов (`RequisitesTab`). */
const WIDE_TYPES = new Set(['complex', 'array', 'doc-ref', 'doc-array', 'image', 'file', 'text']);

/** Поля, занимающие обе колонки сетки — тот же набор, что в редакторе реквизитов (`RequisitesTab`). */

export function QualityDocForm({ allDocTypes, scope, scopeId, initial, onSaved, onCancel }: {
  allDocTypes: DocumentType[]; scope: CatalogScope; scopeId: string | null;
  initial?: QualityDocument | null;
  onSaved: (doc: QualityDocument) => void; onCancel: () => void;
}) {
  const isEdit = !!initial;
  const qualityTypes = useMemo(
    () => allDocTypes.filter(dt => dt.kind === 'Document' && !dt.isAbstract && typeHasTag(dt, FUNCTIONAL_TAG.typeQualityDocument, allDocTypes)),
    [allDocTypes],
  );
  const [typeId, setTypeId] = useState(initial?.documentTypeId ?? qualityTypes[0]?.id ?? '');
  const [displayName, setDisplayName] = useState(initial?.displayName ?? '');
  const [values, setValues] = useState<Record<string, unknown>>(initial?.requisites ?? {});
  const [scan, setScan] = useState<{ blobPath: string; fileName: string; mimeType: string } | null>(
    initial?.scanBlobPath ? { blobPath: initial.scanBlobPath, fileName: initial.scanFileName ?? 'скан', mimeType: initial.scanMimeType ?? 'application/octet-stream' } : null,
  );
  const [uploading, setUploading] = useState(false);
  const [recognizing, setRecognizing] = useState(false);
  // Нарушения ограничений по путям («ПериодДействия.До»); сбрасываются при правке значения, иначе
  // претензия висела бы на исправленном поле (issue #655).
  const [constraintErrors, setConstraintErrors] = useState<Record<string, string>>({});
  /**
   * Поля, которые заполнило распознавание и которых человек ещё не касался (issue #807). Сразу после
   * подстановки распознанное неотличимо от набранного руками, а проверять надо именно его: это
   * данные вероятностные. Состояние сессионное — вечная маркировка происхождения потребовала бы
   * признака на каждом реквизите в базе.
   */
  const [recognizedKeys, setRecognizedKeys] = useState<Set<string>>(() => new Set());
  const scanInputRef = useRef<HTMLInputElement>(null);
  // Картинка поля ещё едет в хранилище — сохранять рано: значение появится только после (issue #522).
  // Своё `uploading` выше — про скан документа качества, это разные загрузки.
  const imageUploading = useUploadsInFlight();
  const [error, setError] = useState('');

  const create = useCreateQualityDoc();
  const update = useUpdateQualityDoc();
  const setScanMut = useSetQualityDocScan();

  const docType = allDocTypes.find(dt => dt.id === typeId);
  const fields = docType ? resolveEffectiveFields(docType, allDocTypes) : [];
  const busy = create.isPending || update.isPending || setScanMut.isPending;

  // Определения типов полей для скалярного редактора (issue #652): без них перечисление из реестра
  // (#59) показывает «Нет вариантов» на заполненном поле, а примитив на базе даты (#60) — обычный
  // текст без календаря и точности. Ровно как в редакторе реквизитов и в каталоге общих данных.
  const { data: primitiveTypes = [] } = useListPrimitiveTypes();
  const { data: enumTypes = [] } = useListEnumTypes();
  const primitiveDef = (f: SchemaField) =>
    f.type === 'primitive' ? primitiveTypes.find(pt => pt.id === f.typeId) : undefined;
  const enumDef = (f: SchemaField) =>
    f.type === 'enum' ? enumTypes.find(et => et.id === f.typeId) : undefined;

  /**
   * Правка значения сразу и проверяется, и снимает прежнюю претензию (issue #655) — как в двух
   * соседних формах. Проверка на изменение ловит ввод, сбор при сохранении — всё остальное:
   * распознавание, вставку, значение, пришедшее с базового документа.
   */
  function setValue(key: string, v: unknown) {
    setValues(p => ({ ...p, [key]: v }));
    // Пометка гаснет при первом касании: она говорит «проверь то, что тебе подставили», и, как
    // только человек поле тронул, вопрос закрыт — держать её дальше значит превращать подсказку
    // в украшение, которое перестают замечать.
    setRecognizedKeys(prev => {
      if (!prev.has(key)) return prev;
      const next = new Set(prev);
      next.delete(key);
      return next;
    });
    const f = fields.find(x => x.key === key);
    const def = f && f.type === 'primitive' ? primitiveDef(f) : undefined;
    const err = def ? validateConstraint(v, def) : null;
    setConstraintErrors(prev => {
      if (!err && !prev[key]) return prev;              // ничего не менялось — не будим рендер
      const next = { ...prev };
      if (err) next[key] = err; else delete next[key];
      return next;
    });
  }

  async function handleScan(file: File) {
    setUploading(true); setError('');
    try { const a = await uploadAttachment(file); setScan({ blobPath: a.blobPath, fileName: a.fileName, mimeType: a.mimeType }); }
    catch (e: unknown) { setError(e instanceof Error ? e.message : 'Ошибка загрузки'); }
    finally { setUploading(false); }
  }

  async function handleRecognize() {
    if (!scan) return;
    setRecognizing(true); setError('');
    try {
      // 1) Подбираем наиболее подходящий тип документа по скану.
      let activeTypeId = typeId;
      if (qualityTypes.length > 1) {
        try {
          const cls = (await recognizeDocument({
            blobPath: scan.blobPath, mimeType: scan.mimeType, silent: true,
            fields: [{ path: '__type__', title: 'Тип документа — выбери наиболее подходящий из вариантов', type: 'enum', options: qualityTypes.map(t => t.name) }],
          })).values;
          const picked = cls['__type__'];
          if (picked) {
            const norm = (s: string) => s.trim().toLowerCase();
            const m = qualityTypes.find(t => norm(t.name) === norm(picked))
              ?? qualityTypes.find(t => norm(picked).includes(norm(t.name)) || norm(t.name).includes(norm(picked)));
            if (m) { activeTypeId = m.id; setTypeId(m.id); }
          }
        } catch { /* классификация не критична — продолжаем с текущим типом */ }
      }
      // 2) Распознаём поля выбранного типа + краткое наименование (бренд производителя + тип продукции).
      const SUMMARY = '__summary__';
      const activeType = allDocTypes.find(d => d.id === activeTypeId);
      const activeFields = activeType ? resolveEffectiveFields(activeType, allDocTypes) : fields;
      // Модели показываем варианты перечислений и базовый тип примитива, ответ отображаем обратно
      // в коды (issue #654) — иначе в реквизиты ложится подпись, которой нет ни в одном пункте.
      const plan = buildRecognitionFields(activeFields, allDocTypes, { primitiveTypes, enumTypes });
      // Реестр перечислений ещё не доехал — распознавать вслепую нельзя: модель ответит подписью,
      // а отобразить её в код будет нечем, и в реквизиты ляжет значение вне списка вариантов.
      if (plan.unresolvedEnums.length > 0) {
        setError('Справочник перечислений ещё загружается — повторите распознавание через мгновение.');
        return;
      }
      const rec = await recognizeDocument({
        blobPath: scan.blobPath, mimeType: scan.mimeType,
        fields: [
          ...plan.fields,
          { path: SUMMARY, title: 'Краткое наименование документа: краткое имя (бренд) производителя и тип продукции, например «EKF — автоматические выключатели»', type: 'string' },
        ],
      });
      const recognized = codesFromLabels(rec.values, plan.enumCodes);
      const summary = (recognized[SUMMARY] ?? '').trim();
      const { [SUMMARY]: _omitSummary, ...fieldValues } = recognized;
      // Число страниц берём из файла → в поле с тэгом doc.pageCount (напр. «КоличествоЛистов»).
      if (rec.pageCount != null && activeType) {
        const p = findTaggedFieldPath(activeType, FUNCTIONAL_TAG.docPageCount, allDocTypes);
        if (p) fieldValues[p.join('.')] = String(rec.pageCount);
      }
      setValues(v => applyRecognized(v, fieldValues));
      setRecognizedKeys(recognizedFieldKeys(fieldValues));
      if (summary) setDisplayName(summary);
    } catch (e: unknown) {
      const resp = (e as { response?: { data?: { error?: string; limit?: boolean } } })?.response;
      if (resp?.data?.limit) setError('Лимит LLM исчерпан — повторите распознавание позже.');
      else setError(resp?.data?.error ?? (e instanceof Error ? e.message : 'Ошибка распознавания'));
    } finally { setRecognizing(false); }
  }

  async function handleSave() {
    setError('');
    const name = displayName.trim() || String(values['НомерДокумента'] ?? '').trim() || docType?.name || 'Документ качества';

    // Ограничения примитивов эта форма не проверяла никогда (issue #655), в отличие от редактора
    // реквизитов и формы общих данных. Тем же сбором, что и они: вторая реализация правил разошлась
    // бы с первой, а серверной проверки нет вовсе — значение уходит в базу как набрали.
    const violations = collectConstraintViolations(values, fields, allDocTypes, primitiveTypes);
    const firstBad = Object.entries(violations)[0];
    if (firstBad) {
      const [path, message] = firstBad;
      setConstraintErrors(violations);
      setError(`${describeViolationPath(path, fields, allDocTypes)} — ${message}`);
      return;
    }
    setConstraintErrors({});

    try {
      let doc: QualityDocument;
      if (isEdit && initial) {
        doc = await update.mutateAsync({ id: initial.id, documentTypeId: typeId, displayName: name, requisites: values });
        if ((scan?.blobPath ?? null) !== (initial.scanBlobPath ?? null))
          doc = await setScanMut.mutateAsync({ id: initial.id, scanBlobPath: scan?.blobPath ?? null, scanFileName: scan?.fileName ?? null, scanMimeType: scan?.mimeType ?? null });
      } else {
        doc = await create.mutateAsync({
          documentTypeId: typeId, displayName: name, requisites: values,
          scope, scopeId, source: 'Manual',
          scanBlobPath: scan?.blobPath, scanFileName: scan?.fileName, scanMimeType: scan?.mimeType,
        });
      }
      onSaved(doc);
    // apiError, а не e.message: у 409 (имя занято, issue #588) объяснение лежит в теле ответа, а
    // e.message даёт «Request failed with status code 409» — то есть ровно ничего.
    } catch (e: unknown) { setError(apiError(e, 'Ошибка сохранения')); }
  }

  return (
    <div className="space-y-3">
      {/* Тип документа — своей строкой, а не в паре с названием (issue #565): он решает, какие поля
          нарисуются ниже, то есть управляет всей остальной формой. Соседство с рядовым текстовым
          полем читалось как «два равных реквизита». */}
      <TypePickerField className="w-full" label="Тип документа" title="Тип документа"
        placeholder="Выберите тип…"
        types={qualityTypes.map<PickType>(dt => ({ id: dt.id, name: dt.name, code: dt.code, section: 'Документы качества' }))}
        value={typeId || undefined}
        onChange={id => { if (id) setTypeId(id); }} />
      <TextField label="Название в библиотеке" value={displayName}
        onChange={e => setDisplayName(e.target.value)} hint="авто из номера" />

      {/* Кнопка, открывающая файловый диалог, — скрытый input + `Button` (как в `DataSetsResource`):
          сам `<label for>` не даёт ни формы-пилюли, ни кольца фокуса. «Распознать скан» — tonal, а не
          заливка: единственная filled в модалке — «Сохранить» (иерархия MD3, см. `Button.tsx`). */}
      <div className="flex items-center gap-2 flex-wrap">
        <input ref={scanInputRef} type="file" className="hidden" accept="image/*,application/pdf,.tif,.tiff"
          onChange={e => {
            const f = e.target.files?.[0];
            e.target.value = ''; // иначе повторный выбор того же файла не даёт onChange
            if (f) void handleScan(f);
          }} />
        <Button variant="outlined" size="sm" icon={<Upload size={14} />} loading={uploading}
          onClick={() => scanInputRef.current?.click()}>
          Скан-копия
        </Button>
        {scan && <span className="text-xs text-fg3 truncate max-w-[180px]" title={scan.fileName}>{scan.fileName}</span>}
        {scan && (
          <Button variant="text" size="sm" icon={<Eye size={14} />}
            onClick={() => void openAttachmentInNewTab(scan.blobPath)}>
            Просмотр
          </Button>
        )}
        {scan && (
          <Button variant="tonal" size="sm" icon={<ShieldCheck size={14} />} loading={recognizing}
            onClick={handleRecognize}>
            Распознать скан
          </Button>
        )}
      </div>

      {/* Раскладка как в редакторе реквизитов: простые поля по два в ряд, широкие — на обе колонки.
          Ключ схемы в подписи не показываем — он нужен настройщику типа, а не тому, кто заполняет. */}
      <div className="grid grid-cols-2 gap-x-4 gap-y-4 border-t border-muted pt-3">
        {fields.map(f => {
          const v = values[f.key];
          // Тонкая левая полоска, а не значок: полей в форме бывает сорок, а в плотной сетке в две
          // колонки значок съедает ширину у самого значения.
          const mark = recognizedKeys.has(f.key) ? ' border-l-2 border-brand pl-2' : '';
          const cls = (WIDE_TYPES.has(f.type) ? 'col-span-2' : 'col-span-1 min-w-0') + mark;
          const label = (
            <label className="block text-xs font-medium text-fg2 mb-1">{f.title}
              {f.required && <span className="ml-0.5 text-danger">*</span>}</label>
          );
          if (f.type === 'complex')
            return <div key={f.key} className={cls} title={recognizedKeys.has(f.key) ? RECOGNIZED_HINT : undefined}>{label}<ComplexFieldGroup field={f} allDocTypes={allDocTypes} value={v}
              onChange={x => setValue(f.key, x)} showValidation={false} docRefMode="catalog" scope={scope} scopeId={scopeId} /></div>;
          if (f.type === 'array')
            return <div key={f.key} className={cls} title={recognizedKeys.has(f.key) ? RECOGNIZED_HINT : undefined}>{label}<ArrayFieldEditor field={f} allDocTypes={allDocTypes} value={v}
              onChange={x => setValue(f.key, x)} showValidation={false} docRefMode="catalog" scope={scope} scopeId={scopeId} /></div>;
          if (f.type === 'doc-ref')
            return <div key={f.key} className={cls} title={recognizedKeys.has(f.key) ? RECOGNIZED_HINT : undefined}>{label}<DocRefCatalogPickerField field={f} allDocTypes={allDocTypes} value={v}
              onChange={x => setValue(f.key, x ?? undefined)} scope={scope} scopeId={scopeId} /></div>;
          if (f.type === 'image')
            return <div key={f.key} className={cls} title={recognizedKeys.has(f.key) ? RECOGNIZED_HINT : undefined}>{label}<ImageField value={v} onChange={x => setValue(f.key, x)} /></div>;
          if (f.type === 'file')
            return <div key={f.key} className={cls} title={recognizedKeys.has(f.key) ? RECOGNIZED_HINT : undefined}>{label}<FileField value={v} onChange={x => setValue(f.key, x ?? undefined)} /></div>;
          return (
            <div key={f.key} className={cls} title={recognizedKeys.has(f.key) ? RECOGNIZED_HINT : undefined}>
              <PrimitiveInput field={f} value={v} label={f.title}
                onChange={x => setValue(f.key, x)} invalid={!!constraintErrors[f.key]}
                primitiveTypeDef={primitiveDef(f)} enumTypeDef={enumDef(f)} />
              {constraintErrors[f.key] && (
                <p className="text-xs text-danger mt-0.5">{constraintErrors[f.key]}</p>
              )}
            </div>
          );
        })}
      </div>

      {error && <p className="text-sm text-danger">{error}</p>}
      <div className="flex items-center gap-2 pt-1">
        <Button variant="filled" onClick={handleSave} loading={busy}
          disabled={busy || recognizing || uploading || imageUploading || !typeId} title={imageUploading ? "Дождитесь загрузки изображения" : undefined}>
          {busy ? 'Сохранение…' : isEdit ? 'Сохранить' : 'Создать'}
        </Button>
        <Button variant="text" onClick={onCancel}>Отмена</Button>
        {recognizing && (
          <span className="flex items-center gap-1.5 text-xs text-fg3">
            <Loader2 size={12} className="animate-spin" /> Идёт распознавание — дождитесь завершения перед сохранением…
          </span>
        )}
      </div>
    </div>
  );
}
