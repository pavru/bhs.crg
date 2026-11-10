import { useEffect, useImperativeHandle, useRef, useState, type Ref } from 'react';
import { OpenInFull, RightPanelClose, RightPanelOpen } from '@/shared/ui/icons';
import { IconButton } from '@/shared/ui/Button';
import { loadInvoiceScan } from '@/shared/api/invoices';
import { loadInvoiceImage, useInvoiceImage } from '@/shared/api/invoiceImage';
import { PdfViewerOverlay } from '@/shared/ui/viewer/PdfViewer';
import { preloadPdfViewer } from '@/shared/ui/viewer/viewerGate';
import { fileShownAs, nameShownAs, useFileKinds } from '@/shared/api/fileKinds';
import { useSnackbar } from '@/shared/ui/Snackbar';
import { IMAGE_CAVEAT, OriginalButton } from './InvoiceFileImage';
import { saveScan } from './scanView';
import { FILE_OVER_REASON, type InvoicePlace } from './invoicePlace';

/**
 * «Файл» в сводке счёта (costs-invoice-screen-10-layout, D-10.3, ТЗ COST-6.2 и COST-6.3; значок по месту —
 * costs-invoice-file-button-icon).
 *
 * <p>Рядом с формой место есть — кнопка открывает и закрывает панель файла справа, и выбор помнится
 * (`onToggle`). Места нет — открывает файл просмотрщиком ПОВЕРХ формы, а причину называет подсказка: молча
 * пропавшая панель читалась бы как «файла нет». Слой поверх живёт до Esc или «Закрыть»: форма под ним
 * остаётся с набранным, и выбор «рядом» он не пишет.</p>
 *
 * <p>Поверх — просмотрщиком, а не вкладкой (issue #1491, D4 зонтика viewer): окно, открытое после загрузки,
 * блокировщик всплывающих окон резал молча, а на Android 14 и ниже вкладка с PDF — это скачивание. Слой от
 * жеста не зависит, поэтому байты грузятся после нажатия. Для Excel и Word поверх формы встаёт вид для
 * чтения — с оговоркой строкой над листом. Файл, который показать нечем, скачивается.</p>
 */
/** «Показать файл» — для метки «со скана» в поле (11, D-11.4): открыть, а не переключить. */
export interface FileShow {
  show: () => void;
}

export function InvoiceFileButton({ invoiceId, blobPath, fileName, file, onToggle, onReveal, showRef }: {
  invoiceId: string; blobPath: string | null; fileName: string | null;
  file: InvoicePlace['file'];
  /** Рядом с формой: открыть или закрыть панель. */
  onToggle: () => void;
  /** Файл уже открыт рядом — показать его панель (страница переводит в неё фокус). */
  onReveal?: () => void;
  showRef?: Ref<FileShow>;
}) {
  const [busy, setBusy] = useState(false);
  const [open, setOpen] = useState(false);
  // Что стоит в слое. Держится и после закрытия: иначе закрывающееся окно мигнуло бы «загружается».
  const [shown, setShown] = useState<{ key: string; source: Blob | null; title: string; reading: boolean } | null>(null);
  const kinds = useFileKinds();
  const toast = useSnackbar();
  const image = useInvoiceImage(invoiceId, blobPath);
  const fileKey = `${invoiceId}|${blobPath ?? ''}`;
  const name = fileName ?? 'Файл счёта';
  // Какое открытие слоя последнее. Ответ прежнего — и удача, и отказ — слоя не трогает: иначе вид для
  // чтения, отказавший после закрытия, закрыл бы уже открытый поверх формы файл.
  const latest = useRef(0);

  const openReading = () => {
    const asked = ++latest.current;
    // Вид — всегда PDF: слой встаёт сразу, «загружается», и вид приходит в него по готовности.
    preloadPdfViewer();
    setShown({ key: `${fileKey}|view`, source: null, title: `${name} — вид для чтения`, reading: true });
    setOpen(true);
    loadInvoiceImage(invoiceId)
      .then(source => { if (latest.current === asked) setShown(now => (now ? { ...now, source } : now)); })
      .catch(e => {
        if (latest.current !== asked) return;
        setOpen(false);
        toast.apiError(e, 'Вид для чтения не открыт');
      });
  };

  const openFile = async () => {
    const asked = ++latest.current;
    // PDF по имени — ядро просмотрщика грузится вместе с файлом, а не после него.
    if (nameShownAs(kinds.data, fileName) === 'pdf') preloadPdfViewer();
    setBusy(true);
    try {
      const { blob, mimeType } = await loadInvoiceScan(invoiceId);
      // Реестра нет (не пришёл или запрос встал) — спрашиваем ещё раз, прямо сейчас.
      const info = kinds.data ?? (await kinds.refetch()).data;
      // Файл, который показать нечем, не открывается — скачивается (issue #1265). Так же и файл, про
      // который узнать не удалось, — но тогда причина названа: иначе PDF, вдруг ушедший в загрузки
      // вместо показа, выглядел бы как поломка кнопки.
      if (!info) {
        saveScan(blob, fileName, mimeType, info);
        toast.info('Файл скачан, а не открыт: список видов файлов с сервера не пришёл, и чем его показать, неизвестно.');
      } else if (fileShownAs(info, mimeType) === 'download') saveScan(blob, fileName, mimeType, info);
      else if (latest.current === asked) {
        setShown({ key: fileKey, source: blob, title: name, reading: false });
        setOpen(true);
      }
    } catch (e) { toast.apiError(e, 'Файл не открыт'); }
    finally { setBusy(false); }
  };

  const over = () => {
    const state = image.data?.state;
    // Вид для чтения построен — он и встаёт поверх (Excel, Word). Не построен — показывать нечем, кроме
    // оригинала: он скачивается, а причина названа.
    if (state === 'built') openReading();
    else if (state === 'refused') {
      toast.info(`Вид для чтения не построен: ${image.data?.reason ?? 'причина не названа'}. Скачан оригинал.`);
      void loadInvoiceScan(invoiceId).then(({ blob, mimeType }) => saveScan(blob, fileName, mimeType, kinds.data))
        .catch(e => toast.apiError(e, 'Оригинал не скачан'));
    } else void openFile();
  };

  const beside = file === 'beside';
  // Значок называет, что сделает нажатие ([COST-6.3], issue #1874): открыть панель, закрыть её или развернуть
  // файл поверх формы. Подпись и `aria-pressed` от значка не зависят — имя переключателя не меняется.
  const icon = file === 'over' ? OpenInFull : beside ? RightPanelClose : RightPanelOpen;
  // Пока виды файлов и вид для чтения не пришли, решать, чем показывать, не по чему: поверх формы открылся бы
  // не вид для чтения, а скачанный оригинал. И повторный щелчок не начинает второе открытие (ревью PR #1794).
  const waiting = file === 'over' && (kinds.isLoading || image.isLoading);
  const loading = busy || waiting;
  // «Показать» — все три места здесь, одним решением с кнопкой: открытую рядом панель не закрывает. Пока виды не
  // пришли, показ ждёт их, а не пропадает: «Показать на скане» нажат в листе, который уже закрылся, и молча
  // проглоченное нажатие не повторить глазами (ревью PR #1794). Открытие в пути второго не начинает.
  const queued = useRef(false);
  const showNow = useRef(() => {});
  useEffect(() => {
    showNow.current = () => {
      if (busy) return;
      if (file === 'over') {
        if (waiting) { queued.current = true; return; }
        over();
      } else if (file === 'closed') onToggle();
      else onReveal?.();
    };
  });
  useEffect(() => {
    if (waiting || !queued.current) return;
    queued.current = false;
    showNow.current();
  }, [waiting]);
  // Хэндл один на всю жизнь кнопки: показ берёт текущее состояние из `showNow` — его обновляет каждый рендер.
  useImperativeHandle(showRef, () => ({ show: () => showNow.current() }), []);
  return (
    <>
      <IconButton icon={icon} label="Файл" aria-pressed={file === 'over' ? undefined : beside}
        title={file === 'over' ? FILE_OVER_REASON : beside ? 'Скрыть файл' : 'Показать файл рядом с формой'}
        loading={loading}
        onClick={file === 'over' ? over : onToggle} />
      {shown && (
        <PdfViewerOverlay open={open} onOpenChange={setOpen} source={shown.source} documentKey={shown.key}
          title={shown.title} rememberZoomAs="invoice-overlay"
          note={shown.reading ? <span data-testid="image-caveat">Вид для чтения — {IMAGE_CAVEAT}.</span> : undefined}
          actions={<OriginalButton invoiceId={invoiceId} fileName={fileName} label="Оригинал" />} />
      )}
    </>
  );
}
