import { useEffect, useState } from 'react';
import { CloudOff, Download, FileSpreadsheet, FileX, Maximize2, RotateCw } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { loadInvoiceScan } from '@/shared/api/invoices';
import { loadInvoiceImage, useRebuildInvoiceImage, type InvoiceImage } from '@/shared/api/invoiceImage';
import { useFileKinds } from '@/shared/api/fileKinds';
import { formatDateTime } from '@/shared/format/format';
import { useToast } from '@/shared/ui/Toast';
import { saveScan } from './scanView';

/**
 * Вид для чтения файла счёта (issue #1270): Excel и Word рядом с формой стоят не сами, а PDF,
 * который из них построил сервер.
 *
 * <p>⚠️ Панель обязана говорить, что это перевёрстка, — постоянной строкой, а не подсказкой: иначе
 * человек сверяет поля по приближённой картинке, считая её документом. По той же причине скачивается
 * отсюда всегда оригинал, и кнопка так и называется.</p>
 */
export const IMAGE_CAVEAT = 'вёрстка может отличаться, документ — оригинал';

/** Скачать приложенный файл — сам, а не его вид. */
export function OriginalButton({ invoiceId, fileName, label }: {
  invoiceId: string; fileName: string | null; label: string;
}) {
  const [busy, setBusy] = useState(false);
  const kinds = useFileKinds();
  const toast = useToast();
  return (
    <Button size="sm" loading={busy} icon={<Download size={13} />}
      title={fileName ? `Скачать оригинал: ${fileName}` : 'Скачать оригинал'}
      onClick={async () => {
        setBusy(true);
        try {
          const { url, mimeType } = await loadInvoiceScan(invoiceId);
          saveScan(url, fileName, mimeType, kinds.data);
          // С отсрочкой: браузеру нужна живая ссылка, пока он пишет файл.
          setTimeout(() => URL.revokeObjectURL(url), 60_000);
        } catch (e) { toast.apiError(e, 'Файл не скачан'); }
        finally { setBusy(false); }
      }}>
      {label}
    </Button>
  );
}

/** Построенный вид: строка-оговорка, пометки построителя и сам PDF. */
export function BuiltImage({ invoiceId, blobPath, fileName, image, canEdit }: {
  invoiceId: string; blobPath: string | null; fileName: string | null; image: InvoiceImage; canEdit: boolean;
}) {
  const { url, failed } = useImageUrl(invoiceId, `${blobPath ?? ''}|${image.builtAt ?? ''}`);
  const rebuild = useRebuildInvoiceImage(invoiceId, blobPath);
  const toast = useToast();

  return (
    <div className="h-full flex flex-col min-h-0">
      <div className="flex items-center gap-2 px-3 py-2 border-b border-stroke shrink-0">
        <FileSpreadsheet size={14} className="text-fg3 shrink-0" />
        <span className="text-xs text-fg2 truncate flex-1">{fileName ?? 'Файл счёта'}</span>
        <Button size="sm" icon={<Maximize2 size={13} />} disabled={!url}
          onClick={() => { if (url) window.open(url, '_blank'); }}>
          Во весь экран
        </Button>
        <OriginalButton invoiceId={invoiceId} fileName={fileName} label="Оригинал" />
      </div>
      <div className="px-3 py-1.5 border-b border-stroke bg-surface2 text-xs text-fg2 shrink-0" data-testid="image-caveat">
        <div className="flex items-baseline gap-2">
          <span className="flex-1"
            title={`Построен ${image.builtAt ? formatDateTime(image.builtAt) : ''} из файла${fileName ? ` «${fileName}»` : ''}. `
              + 'Текст и числа взяты из файла; расположение, шрифты и разбивка на страницы — приближённые.'}>
            <span className="font-medium text-fg1">Вид для чтения</span>
            {image.pages ? ` · ${image.pages} стр.` : ''} — {IMAGE_CAVEAT}.
          </span>
          {canEdit && (
            <Button size="sm" variant="text" loading={rebuild.isPending} icon={<RotateCw size={12} />}
              title="Построить вид заново. Сам файл и распознанное остаются как были."
              onClick={() => rebuild.mutateAsync()
                .then(next => next.state === 'refused'
                  ? toast.info(`Вид не перестроен. ${next.reason ?? ''} Показан прежний.`)
                  : toast.success('Вид для чтения перестроен.'))
                .catch(e => toast.apiError(e, 'Вид не перестроен'))}>
              {rebuild.isPending ? 'Перестраиваем…' : 'Перестроить'}
            </Button>
          )}
        </div>
        {image.notes.length > 0 && (
          <ul className="mt-1 space-y-0.5 text-fg2">
            {image.notes.map(note => <li key={note}>{note}</li>)}
          </ul>
        )}
      </div>
      <div className="flex-1 min-h-0 bg-base">
        {failed && (
          <Centered>
            Вид для чтения не загрузился. Он построен — значит дело в связи или в хранилище: обновите
            страницу. Сам файл скачивается кнопкой «Оригинал».
          </Centered>
        )}
        {!failed && !url && <Centered>Вид для чтения загружается…</Centered>}
        {url && <iframe src={url} title={`${fileName ?? 'Файл счёта'} — вид для чтения`} className="w-full h-full border-0 bg-white" />}
      </div>
    </div>
  );
}

/**
 * Вид не построен. Файл при этом ПРИЛОЖЕН и цел — это сказано прямо: отказ построения не отказ
 * загрузки, и человек не должен прикладывать файл второй раз.
 *
 * <p>Два разных отказа выглядят по-разному: «файл такой» (пароль, пуст) — повторять нечего, совет в
 * самой причине; «сервис не ответил» — дело не в файле, и есть «Повторить».</p>
 */
export function RefusedImage({ invoiceId, fileName, image, retrying, onRetry }: {
  invoiceId: string; fileName: string | null; image: InvoiceImage; retrying: boolean; onRetry: () => void;
}) {
  return (
    <div className="h-full flex flex-col min-h-0">
      <div className="flex items-center gap-2 px-3 py-2 border-b border-stroke shrink-0">
        <FileSpreadsheet size={14} className="text-fg3 shrink-0" />
        <span className="text-xs text-fg2 truncate flex-1">{fileName ?? 'Файл счёта'}</span>
        <OriginalButton invoiceId={invoiceId} fileName={fileName} label="Оригинал" />
      </div>
      <div className="flex-1 min-h-0 bg-base">
        <Centered>
          <div className="flex flex-col items-center gap-2 max-w-sm" data-testid="image-refused">
            {image.retryHelps
              ? <CloudOff size={20} className="text-fg3" />
              : <FileX size={20} className="text-warning" />}
            <span className="text-sm font-medium text-fg1">
              {image.retryHelps ? 'Вид для чтения пока не построен' : 'Вид для чтения не построен'}
            </span>
            <span>{image.reason}</span>
            <span>
              {image.retryHelps
                ? 'Дело не в файле — он приложен и цел.'
                : 'Показать и распознать его нельзя. Сам файл приложен и цел.'}
            </span>
            <div className="flex gap-2 mt-1">
              {image.retryHelps && (
                <Button size="sm" variant="outlined" loading={retrying} icon={<RotateCw size={13} />} onClick={onRetry}>
                  Повторить
                </Button>
              )}
              <OriginalButton invoiceId={invoiceId} fileName={fileName} label="Скачать оригинал" />
            </div>
          </div>
        </Centered>
      </div>
    </div>
  );
}

export function Centered({ children }: { children: React.ReactNode }) {
  return <div className="h-full grid place-items-center p-4 text-xs text-fg3 text-center">{children}</div>;
}

/** Вид одним чтением, с освобождением объектной ссылки; перечитывается, когда вид перестроен. */
function useImageUrl(invoiceId: string, key: string) {
  const [loaded, setLoaded] = useState<{ key: string; url: string | null; failed: boolean } | null>(null);
  const full = `${invoiceId}|${key}`;

  useEffect(() => {
    let cancelled = false;
    let objectUrl: string | null = null;

    loadInvoiceImage(invoiceId)
      .then(url => {
        objectUrl = url;
        if (cancelled) { URL.revokeObjectURL(url); return; }
        setLoaded({ key: full, url, failed: false });
      })
      .catch(() => { if (!cancelled) setLoaded({ key: full, url: null, failed: true }); });

    return () => {
      cancelled = true;
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [invoiceId, full]);

  // Чужой ответ не показываем: при замене файла или перестроении на миг встал бы прежний вид.
  return loaded?.key === full ? loaded : { url: null, failed: false };
}
