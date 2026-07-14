import { useState, useMemo } from 'react';
import { Loader2, ShieldCheck, Upload, Eye } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { Select, SelectItem } from '@/shared/ui/Select';
import { TextField } from '@/shared/ui/TextField';
import {
  useCreateQualityDoc, useUpdateQualityDoc, useSetQualityDocScan, recognizeDocument,
  type QualityDocument, type RecognitionFieldReq,
} from '@/shared/api/qualityDocs';
import { uploadAttachment, openAttachmentInNewTab } from '@/shared/api/attachments';
import type { DocumentType, CatalogScope } from '@/shared/api/types';
import { resolveEffectiveFields, typeHasTag, findTaggedFieldPath, type SchemaField } from '@/shared/api/schema';
import { FUNCTIONAL_TAG } from '@/shared/api/tags';
import {
  PrimitiveInput, ComplexFieldGroup, ArrayFieldEditor, DocRefCatalogPickerField, ImageField, FileField,
} from '@/features/document-sets/fields';

/** Разворачивает поля типа в плоские «листья» (путь через точку) для распознавания. */
export function flattenLeaves(fields: SchemaField[], allDocTypes: DocumentType[], prefix = '', depth = 0): RecognitionFieldReq[] {
  if (depth > 3) return [];
  const out: RecognitionFieldReq[] = [];
  for (const f of fields) {
    const path = prefix ? `${prefix}.${f.key}` : f.key;
    if (f.type === 'complex' && f.typeId) {
      const ct = allDocTypes.find(d => d.id === f.typeId);
      if (ct) out.push(...flattenLeaves(resolveEffectiveFields(ct, allDocTypes), allDocTypes, path, depth + 1));
    } else if (['array', 'doc-ref', 'doc-array', 'image', 'file'].includes(f.type)) {
      // для распознавания пропускаем
    } else {
      out.push({ path, title: f.title, type: f.type, options: f.options });
    }
  }
  return out;
}

/** Раскладывает плоские значения (путь через точку) во вложенный объект и сливает с текущими. */
export function applyRecognized(values: Record<string, unknown>, flat: Record<string, string>): Record<string, unknown> {
  const next: Record<string, unknown> = JSON.parse(JSON.stringify(values ?? {}));
  for (const [path, val] of Object.entries(flat)) {
    if (val == null || String(val).trim() === '') continue;
    const parts = path.split('.');
    let cur = next;
    for (let i = 0; i < parts.length - 1; i++) {
      const k = parts[i];
      if (typeof cur[k] !== 'object' || cur[k] == null) cur[k] = {};
      cur = cur[k] as Record<string, unknown>;
    }
    cur[parts[parts.length - 1]] = val;
  }
  return next;
}

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
  const [error, setError] = useState('');

  const create = useCreateQualityDoc();
  const update = useUpdateQualityDoc();
  const setScanMut = useSetQualityDocScan();

  const docType = allDocTypes.find(dt => dt.id === typeId);
  const fields = docType ? resolveEffectiveFields(docType, allDocTypes) : [];
  const busy = create.isPending || update.isPending || setScanMut.isPending;

  function setValue(key: string, v: unknown) { setValues(p => ({ ...p, [key]: v })); }

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
      const rec = await recognizeDocument({
        blobPath: scan.blobPath, mimeType: scan.mimeType,
        fields: [
          ...flattenLeaves(activeFields, allDocTypes),
          { path: SUMMARY, title: 'Краткое наименование документа: краткое имя (бренд) производителя и тип продукции, например «EKF — автоматические выключатели»', type: 'string' },
        ],
      });
      const recognized = rec.values;
      const summary = (recognized[SUMMARY] ?? '').trim();
      const { [SUMMARY]: _omitSummary, ...fieldValues } = recognized;
      // Число страниц берём из файла → в поле с тэгом doc.pageCount (напр. «КоличествоЛистов»).
      if (rec.pageCount != null && activeType) {
        const p = findTaggedFieldPath(activeType, FUNCTIONAL_TAG.docPageCount, allDocTypes);
        if (p) fieldValues[p.join('.')] = String(rec.pageCount);
      }
      setValues(v => applyRecognized(v, fieldValues));
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
    } catch (e: unknown) { setError(e instanceof Error ? e.message : 'Ошибка'); }
  }

  return (
    <div className="space-y-3">
      <div className="grid grid-cols-2 gap-3">
        <div>
          <label className="block text-xs font-medium text-fg2 mb-1">Тип документа</label>
          <Select value={typeId || undefined} onValueChange={setTypeId}
            placeholder="Выберите тип…" aria-label="Тип документа">
            {qualityTypes.map(dt => <SelectItem key={dt.id} value={dt.id}>{dt.name}</SelectItem>)}
          </Select>
        </div>
        <TextField label="Название в библиотеке" value={displayName}
          onChange={e => setDisplayName(e.target.value)} hint="авто из номера" />
      </div>

      <div className="flex items-center gap-2 flex-wrap">
        <label className="flex items-center gap-1.5 text-sm px-3 py-1.5 border border-stroke rounded-md cursor-pointer hover:bg-base">
          {uploading ? <Loader2 size={14} className="animate-spin" /> : <Upload size={14} />}
          Скан-копия
          <input type="file" className="hidden" accept="image/*,application/pdf,.tif,.tiff"
            onChange={e => { const f = e.target.files?.[0]; if (f) void handleScan(f); }} />
        </label>
        {scan && <span className="text-xs text-fg3 truncate max-w-[180px]">{scan.fileName}</span>}
        {scan && (
          <button type="button" onClick={() => void openAttachmentInNewTab(scan.blobPath)}
            className="flex items-center gap-1.5 text-sm px-3 py-1.5 rounded-md border border-stroke hover:bg-base">
            <Eye size={14} /> Просмотр
          </button>
        )}
        {scan && (
          <button onClick={handleRecognize} disabled={recognizing}
            className="flex items-center gap-1.5 text-sm px-3 py-1.5 rounded-md bg-brand-subtle text-brand hover:bg-brand-subtle disabled:opacity-50">
            {recognizing ? <Loader2 size={14} className="animate-spin" /> : <ShieldCheck size={14} />}
            Распознать скан
          </button>
        )}
      </div>

      <div className="space-y-2 border-t border-muted pt-3">
        {fields.map(f => {
          const v = values[f.key];
          const label = (
            <label className="block text-xs font-medium text-fg2 mb-1">{f.title}
              <span className="ml-2 text-[10px] text-fg4 font-mono">{f.key}</span></label>
          );
          if (f.type === 'complex')
            return <div key={f.key}>{label}<ComplexFieldGroup field={f} allDocTypes={allDocTypes} value={v}
              onChange={x => setValue(f.key, x)} showValidation={false} docRefMode="catalog" scope={scope} scopeId={scopeId} /></div>;
          if (f.type === 'array')
            return <div key={f.key}>{label}<ArrayFieldEditor field={f} allDocTypes={allDocTypes} value={v}
              onChange={x => setValue(f.key, x)} showValidation={false} docRefMode="catalog" scope={scope} scopeId={scopeId} /></div>;
          if (f.type === 'doc-ref')
            return <div key={f.key}>{label}<DocRefCatalogPickerField field={f} allDocTypes={allDocTypes} value={v}
              onChange={x => setValue(f.key, x ?? undefined)} scope={scope} scopeId={scopeId} /></div>;
          if (f.type === 'image')
            return <div key={f.key}>{label}<ImageField value={v} onChange={x => setValue(f.key, x)} /></div>;
          if (f.type === 'file')
            return <div key={f.key}>{label}<FileField value={v} onChange={x => setValue(f.key, x ?? undefined)} /></div>;
          return <div key={f.key}><PrimitiveInput field={f} value={v} label={f.title} onChange={x => setValue(f.key, x)} invalid={false} /></div>;
        })}
      </div>

      {error && <p className="text-sm text-danger">{error}</p>}
      <div className="flex items-center gap-2 pt-1">
        <Button variant="filled" onClick={handleSave} loading={busy} disabled={busy || recognizing || !typeId}>
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
