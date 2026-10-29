import { useState, useEffect } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { CircularProgress } from '@/shared/ui/CircularProgress';
import { Delete, Description, Download, ImageIcon, TableChart, Upload, Visibility } from '@/shared/ui/icons';
import {
  type FileAttachment, isFileAttachment, getFileCategory,
  uploadAttachment, uploadPrintForm, loadAttachmentBlob, loadAttachmentObjectUrl, formatBytes, saveBlob,
} from '@/shared/api/attachments';
import { Button } from '@/shared/ui/Button';
import { Modal } from '@/shared/ui/Modal';
import { Tooltip } from '@/shared/ui/Tooltip';
import { PdfViewer, ViewerTitleBar } from '@/shared/ui/viewer/PdfViewer';
import { preloadPdfViewer } from '@/shared/ui/viewer/viewerGate';

export interface PrintFormContext {
  setId: string;
  instanceId: string;
  fieldKey: string;
  onMetaUpdated: (updates: Record<string, unknown>) => void;
}

export function FileTypeIcon({ mimeType }: { mimeType: string }) {
  if (mimeType.startsWith('image/')) return <Icon icon={ImageIcon} size={18} className="text-tertiary shrink-0" />;
  if (mimeType === 'application/pdf') return <Icon icon={Description} size={18} className="text-error shrink-0" />;
  if (mimeType.includes('word')) return <Icon icon={Description} size={18} className="text-primary shrink-0" />;
  if (mimeType.includes('sheet') || mimeType.includes('excel')) return <Icon icon={TableChart} size={18} className="text-success shrink-0" />;
  return <Icon icon={Description} size={18} className="text-on-surface-variant shrink-0" />;
}

export function FilePreviewModal({ open, onOpenChange, attachment }: {
  open: boolean; onOpenChange: (o: boolean) => void; attachment: FileAttachment;
}) {
  // PDF — общим просмотрщиком (issue #1492): окно без своей шапки, верхняя строка у просмотрщика.
  const pdf = getFileCategory(attachment.mimeType) === 'pdf';
  return (
    <Modal open={open} onOpenChange={onOpenChange} title={attachment.fileName} wide nothingToLose
      headerless={pdf} flushBody={pdf}>
      {/* Тело заводится заново на каждое открытие и на каждый новый файл (issue #858): ключ вместо
          эффекта, гасившего прошлую загрузку тремя setState на старте. Так «идёт загрузка» — это
          начальное состояние свежего компонента, а не состояние, в которое его надо возвращать. */}
      {pdf
        ? <PdfPreviewBody key={attachment.blobPath} attachment={attachment} onClose={() => onOpenChange(false)} />
        : <FilePreviewBody key={attachment.blobPath} attachment={attachment} />}
    </Modal>
  );
}

/**
 * Загрузка вложения для окна предпросмотра. Тело окна заводится заново ключом (issue #858), поэтому
 * «грузится» — начальное состояние: нет ни значения, ни ошибки. Загрузчик и уборка — функции модуля, а
 * не замыкания рендера: иначе эффект перезапускался бы каждым рендером.
 */
function useAttachmentLoad<T>(blobPath: string, load: (blobPath: string) => Promise<T>, dispose?: (value: T) => void) {
  const [value, setValue] = useState<T | null>(null);
  const [error, setError] = useState('');

  useEffect(() => {
    let loaded: T | null = null;
    let cancelled = false;
    load(blobPath)
      .then(v => {
        if (cancelled) { dispose?.(v); return; }
        loaded = v; setValue(v);
      })
      .catch(e => { if (!cancelled) setError(e instanceof Error ? e.message : 'Ошибка загрузки'); });
    return () => { cancelled = true; if (loaded !== null) dispose?.(loaded); };
  }, [blobPath, load, dispose]);

  return { value, error };
}

const revokeObjectUrl = ({ url }: { url: string }) => URL.revokeObjectURL(url);

/**
 * PDF вложения — общим просмотрщиком, а не встроенным просмотрщиком браузера во фрейме (issue #1492):
 * Chrome на Android на месте листа показывал кнопку «Открыть», Safari на iOS — одну первую страницу.
 *
 * Высота области задана явно: без неё лента листов нулевой высоты, и лист пуст без ошибки. На телефоне
 * окно и так во весь экран (`Modal`, узкий экран), и область берёт всё под строкой — второй слой
 * поверх окна был бы лишним.
 */
function PdfPreviewBody({ attachment, onClose }: { attachment: FileAttachment; onClose: () => void }) {
  const { value: blob, error } = useAttachmentLoad(attachment.blobPath, loadAttachmentBlob);
  // Окно знает, что покажет PDF: ядро просмотрщика грузится вместе с байтами, а не после них.
  useEffect(() => preloadPdfViewer(), []);

  return (
    <>
      <ViewerTitleBar title={attachment.fileName} source={blob} onClose={onClose} />
      {/* Своя область наложения и обрезка: лист не выходит за неё поверх строки. */}
      <div data-testid="file-preview-area" className="relative isolate overflow-hidden h-[70vh] max-sm:h-auto max-sm:flex-1 min-h-0">
        {error
          ? <p className="text-sm text-error py-4 text-center">{error}</p>
          : (
            // «Скачать» — выход из отказа «не читается как PDF», который велит скачать оригинал.
            <PdfViewer source={blob} documentKey={attachment.blobPath} title={attachment.fileName}
              actions={blob && (
                <Button size="sm" variant="tonal" icon={<Icon icon={Download} size={18} />}
                  onClick={() => saveBlob(blob, attachment.fileName)}>
                  Скачать
                </Button>
              )} />
          )}
      </div>
    </>
  );
}

function FilePreviewBody({ attachment }: { attachment: FileAttachment }) {
  const { value, error } = useAttachmentLoad(attachment.blobPath, loadAttachmentObjectUrl, revokeObjectUrl);
  const objectUrl = value?.url;
  const category = getFileCategory(attachment.mimeType);

  return (
    <>
      {!value && !error && (
        <div className="flex items-center justify-center py-12">
          <CircularProgress size={24} className="text-primary" />
        </div>
      )}
      {error && <p className="text-sm text-error py-4 text-center">{error}</p>}
      {objectUrl && category === 'image' && (
        <img src={objectUrl} alt={attachment.fileName}
          className="max-w-full max-h-[70vh] object-contain mx-auto block rounded" />
      )}
      {objectUrl && category === 'office' && (
        <div className="flex flex-col items-center gap-4 py-10">
          <Icon icon={TableChart} size={48} className="text-success" />
          <p className="text-sm text-on-surface-variant">Предпросмотр недоступен для этого формата.</p>
          <a href={objectUrl} download={attachment.fileName}
            className="inline-flex items-center gap-2 h-9 px-4 bg-primary state-layer text-on-primary text-sm font-medium rounded-full shadow-[var(--md-sys-elevation-level1)]">
            <Icon icon={Download} size={18} /> Скачать
          </a>
        </div>
      )}
    </>
  );
}

export function FileField({ value, onChange, printForm, readOnly = false }: {
  value: unknown;
  onChange: (val: FileAttachment | null) => void;
  printForm?: PrintFormContext;
  /**
   * Файл кладёт не человек (привязка к источнику, замок модуля — issue #958): показываем вложение
   * и даём его открыть, но выбор и удаление убираем СОВСЕМ — отключённая корзина обещала бы
   * действие, которого не будет.
   */
  readOnly?: boolean;
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
      <div className="flex items-center gap-2 border border-outline-variant rounded-lg px-3 py-2.5">
        <CircularProgress size={18} className="text-primary shrink-0" />
        <span className="text-sm text-on-surface-variant">Загрузка файла...</span>
      </div>
    );
  }

  if (attachment) {
    return (
      <>
        <div className="flex items-center gap-2 border border-outline-variant rounded-lg px-3 py-2 bg-surface-container-low">
          <FileTypeIcon mimeType={attachment.mimeType} />
          <span className="flex-1 text-sm text-on-surface font-medium truncate">{attachment.fileName}</span>
          <span className="text-xs text-on-surface-variant shrink-0">{formatBytes(attachment.size)}</span>
          <Tooltip content="Предпросмотр" name>
            <button type="button" onClick={() => setPreviewOpen(true)}
              className="p-1 text-on-surface-variant hover:text-primary transition-colors shrink-0">
              <Icon icon={Visibility} size={18} />
            </button>
          </Tooltip>
          {!readOnly && (
            <Tooltip content="Удалить" name>
              <button type="button" onClick={() => onChange(null)}
                className="p-1 text-on-surface-variant hover:text-error transition-colors shrink-0">
                <Icon icon={Delete} size={18} />
              </button>
            </Tooltip>
          )}
        </div>
        {uploadError && <p className="text-xs text-error mt-1">{uploadError}</p>}
        <FilePreviewModal open={previewOpen} onOpenChange={setPreviewOpen} attachment={attachment} />
      </>
    );
  }

  if (readOnly)
    return (
      <div className="flex items-center justify-center border border-dashed border-outline-variant rounded-lg py-5">
        <span className="text-xs text-on-surface-variant">Файла нет</span>
      </div>
    );

  return (
    <>
      <label className="flex flex-col items-center justify-center gap-2 border-2 border-dashed border-outline rounded-lg py-5 cursor-pointer hover:border-primary hover:bg-primary-container transition-colors">
        <Icon icon={Upload} size={18} className="text-on-surface-variant" />
        <span className="text-sm text-on-surface-variant">Нажмите для выбора файла</span>
        <span className="text-xs text-on-surface-variant">PDF, DOCX, XLSX, PNG, JPG (до 50 МБ)</span>
        <input type="file" accept=".pdf,.docx,.xlsx,.xls,.png,.jpg,.jpeg,.gif,.webp"
          className="hidden" onChange={handleFile} />
      </label>
      {uploadError && <p className="text-xs text-error mt-1">{uploadError}</p>}
    </>
  );
}
