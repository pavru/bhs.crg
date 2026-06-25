import { useState, useEffect } from 'react';
import {
  FileText, FileSpreadsheet, Image as ImageIcon,
  Eye, Loader2, Trash2, Upload, Download,
} from 'lucide-react';
import {
  type FileAttachment, isFileAttachment, getFileCategory,
  uploadAttachment, uploadPrintForm, loadAttachmentObjectUrl, formatBytes,
} from '@/shared/api/attachments';
import { Modal } from '@/shared/ui/Modal';

export interface PrintFormContext {
  setId: string;
  instanceId: string;
  fieldKey: string;
  onMetaUpdated: (updates: Record<string, unknown>) => void;
}

export function FileTypeIcon({ mimeType }: { mimeType: string }) {
  if (mimeType.startsWith('image/')) return <ImageIcon size={16} className="text-purple-500 shrink-0" />;
  if (mimeType === 'application/pdf') return <FileText size={16} className="text-danger shrink-0" />;
  if (mimeType.includes('word')) return <FileText size={16} className="text-brand shrink-0" />;
  if (mimeType.includes('sheet') || mimeType.includes('excel')) return <FileSpreadsheet size={16} className="text-success shrink-0" />;
  return <FileText size={16} className="text-fg4 shrink-0" />;
}

export function FilePreviewModal({ open, onOpenChange, attachment }: {
  open: boolean; onOpenChange: (o: boolean) => void; attachment: FileAttachment;
}) {
  const [objectUrl, setObjectUrl] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const category = getFileCategory(attachment.mimeType);

  useEffect(() => {
    if (!open) return;
    let url: string | null = null;
    let cancelled = false;
    setLoading(true); setError(''); setObjectUrl(null);
    loadAttachmentObjectUrl(attachment.blobPath)
      .then(res => {
        if (cancelled) { URL.revokeObjectURL(res.url); return; }
        url = res.url; setObjectUrl(res.url);
      })
      .catch(e => { if (!cancelled) setError(e instanceof Error ? e.message : 'Ошибка загрузки'); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; if (url) URL.revokeObjectURL(url); };
  }, [open, attachment.blobPath]);

  return (
    <Modal open={open} onOpenChange={onOpenChange} title={attachment.fileName}>
      {loading && (
        <div className="flex items-center justify-center py-12">
          <Loader2 size={24} className="animate-spin text-brand" />
        </div>
      )}
      {error && <p className="text-sm text-danger py-4 text-center">{error}</p>}
      {objectUrl && category === 'image' && (
        <img src={objectUrl} alt={attachment.fileName}
          className="max-w-full max-h-[70vh] object-contain mx-auto block rounded" />
      )}
      {objectUrl && category === 'pdf' && (
        <iframe src={objectUrl} title={attachment.fileName}
          className="w-full border-0 rounded" style={{ height: '70vh' }} />
      )}
      {objectUrl && category === 'office' && (
        <div className="flex flex-col items-center gap-4 py-10">
          <FileSpreadsheet size={48} className="text-success" />
          <p className="text-sm text-fg3">Предпросмотр недоступен для этого формата.</p>
          <a href={objectUrl} download={attachment.fileName}
            className="flex items-center gap-2 px-4 py-2 bg-brand hover:bg-brand-hover text-white text-sm rounded-md transition-colors">
            <Download size={14} /> Скачать
          </a>
        </div>
      )}
    </Modal>
  );
}

export function FileField({ value, onChange, printForm }: {
  value: unknown;
  onChange: (val: FileAttachment | null) => void;
  printForm?: PrintFormContext;
}) {
  const [uploading, setUploading] = useState(false);
  const [uploadError, setUploadError] = useState('');
  const [previewOpen, setPreviewOpen] = useState(false);
  const attachment = isFileAttachment(value) ? value : null;

  async function handleFile(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    if (!file) return;
    setUploading(true); setUploadError('');
    try {
      if (printForm) {
        const { updatedFields } = await uploadPrintForm(
          file, printForm.setId, printForm.instanceId, printForm.fieldKey,
        );
        const fileVal = updatedFields[printForm.fieldKey];
        onChange(isFileAttachment(fileVal) ? fileVal : null);
        printForm.onMetaUpdated(updatedFields);
      } else {
        const result = await uploadAttachment(file);
        onChange(result);
      }
    } catch (err) {
      setUploadError(err instanceof Error ? err.message : 'Ошибка загрузки');
    } finally {
      setUploading(false);
      e.target.value = '';
    }
  }

  if (uploading) {
    return (
      <div className="flex items-center gap-2 border border-stroke rounded-lg px-3 py-2.5">
        <Loader2 size={14} className="animate-spin text-brand shrink-0" />
        <span className="text-sm text-fg3">Загрузка файла...</span>
      </div>
    );
  }

  if (attachment) {
    return (
      <>
        <div className="flex items-center gap-2 border border-stroke rounded-lg px-3 py-2 bg-base">
          <FileTypeIcon mimeType={attachment.mimeType} />
          <span className="flex-1 text-sm text-fg1 font-medium truncate">{attachment.fileName}</span>
          <span className="text-xs text-fg4 shrink-0">{formatBytes(attachment.size)}</span>
          <button type="button" onClick={() => setPreviewOpen(true)} title="Предпросмотр"
            className="p-1 text-fg4 hover:text-brand transition-colors shrink-0">
            <Eye size={14} />
          </button>
          <button type="button" onClick={() => onChange(null)} title="Удалить"
            className="p-1 text-fg4 hover:text-danger transition-colors shrink-0">
            <Trash2 size={13} />
          </button>
        </div>
        {uploadError && <p className="text-xs text-danger mt-1">{uploadError}</p>}
        <FilePreviewModal open={previewOpen} onOpenChange={setPreviewOpen} attachment={attachment} />
      </>
    );
  }

  return (
    <>
      <label className="flex flex-col items-center justify-center gap-2 border-2 border-dashed border-stroke-strong rounded-lg py-5 cursor-pointer hover:border-brand hover:bg-brand-subtle transition-colors">
        <Upload size={18} className="text-fg4" />
        <span className="text-sm text-fg3">Нажмите для выбора файла</span>
        <span className="text-xs text-fg4">PDF, DOCX, XLSX, PNG, JPG, SVG (до 50 МБ)</span>
        <input type="file" accept=".pdf,.docx,.xlsx,.xls,.png,.jpg,.jpeg,.gif,.webp,.svg"
          className="hidden" onChange={handleFile} />
      </label>
      {uploadError && <p className="text-xs text-danger mt-1">{uploadError}</p>}
    </>
  );
}
