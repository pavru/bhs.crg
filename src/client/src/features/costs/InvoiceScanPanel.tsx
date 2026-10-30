import { useEffect, useRef, useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Description, Download, OpenInFull, OpenInNew, RotateRight, UploadFile } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';
import { loadInvoiceScan } from '@/shared/api/invoices';
import { loadInvoiceImage, useInvoiceImage } from '@/shared/api/invoiceImage';
import { openInNewTab } from '@/shared/ui/viewer/openInNewTab';
import { PdfViewer, PdfViewerOverlay } from '@/shared/ui/viewer/PdfViewer';
import { preloadPdfViewer } from '@/shared/ui/viewer/viewerGate';
import { acceptOf, fileShownAs, nameShownAs, recognizedKinds, shownKinds, useFileKinds, wordsOf } from '@/shared/api/fileKinds';
import { useCan } from '@/shared/api/access';
import { useToast } from '@/shared/ui/Toast';
import { BuiltImage, Centered as Note, IMAGE_CAVEAT, OriginalButton, RefusedImage } from './InvoiceFileImage';
import { saveScan } from './scanView';

/**
 * Файл счёта РЯДОМ с формой (ТЗ COST-6.2, задача C1).
 *
 * <p>Рядом, а не в новой вкладке: человек сверяет форму с бумагой, и переключение вкладок на каждое
 * поле — это и есть та работа, от которой распознавание должно избавлять.</p>
 *
 * <p>⚠️ Документ показывается ЦЕЛИКОМ (решение владельца 29.09.2026, п. 17). Фрагмента с подсветкой
 * места в первой версии нет — и поэтому же отменена проверка «отдаёт ли профиль координаты полей»:
 * ответ ничего не менял бы.</p>
 *
 * <p>Excel и Word показываются видом для чтения, а не сами (issue #1270): что стоит рядом с формой —
 * сам файл, его вид или отказ, — панель спрашивает у сервера первым делом.</p>
 */
export function InvoiceScanPanel({ invoiceId, blobPath, fileName }: {
  invoiceId: string;
  blobPath: string | null;
  fileName: string | null;
}) {
  const image = useInvoiceImage(invoiceId, blobPath);
  const canEdit = useCan().permission('costs.invoice.edit');
  const slow = useSlow(image.isLoading);

  if (image.isLoading) {
    return (
      <Note>
        <span>Файл загружается…</span>
        {/* Долго — значит строится вид: файл приложили до появления видов либо сервер занят. */}
        {slow && <span className="block mt-1">Строим вид для чтения. Большие таблицы строятся до минуты — форму можно заполнять.</span>}
      </Note>
    );
  }

  // ⚠️ Ответа нет — это не «вида нет»: про Excel тогда пришлось бы сказать «показать нечем», и свой
  // отказ был бы выдан за свойство файла. Говорим, что не узнали, и даём и повтор, и сам файл.
  if (!image.data) {
    return (
      <Note>
        <div className="flex flex-col items-center gap-2">
          <span>Чем показать файл, выяснить не удалось: сервер не ответил. Файл приложен — дело в связи, а не в нём.</span>
          <div className="flex gap-2">
            <Button variant="outlined" loading={image.isFetching} icon={<Icon icon={RotateRight} size={18} />}
              onClick={() => void image.refetch()}>
              Повторить
            </Button>
            <OriginalButton invoiceId={invoiceId} fileName={fileName} label="Скачать оригинал" />
          </div>
        </div>
      </Note>
    );
  }

  if (image.data.state === 'built')
    return <BuiltImage invoiceId={invoiceId} blobPath={blobPath} fileName={fileName} image={image.data} canEdit={canEdit} />;
  if (image.data.state === 'refused')
    // «Повторить» — тот же вопрос ещё раз: отказ сервиса сервер не запоминает и строит заново.
    return <RefusedImage invoiceId={invoiceId} blobPath={blobPath} fileName={fileName} image={image.data}
      canEdit={canEdit} retrying={image.isFetching} onRetry={() => void image.refetch()} />;
  return <OriginalFile invoiceId={invoiceId} blobPath={blobPath} fileName={fileName} />;
}

/** Файл, который показывается сам: PDF, изображение — либо тот, который показать нечем. */
function OriginalFile({ invoiceId, blobPath, fileName }: {
  invoiceId: string;
  /** Путь файла в хранилище. ⚠️ Нужен ИМЕННО здесь: после «Заменить файл» счёт тот же, а файл
   *  другой — панель, зависящая от одного счёта, показывала бы ПРЕЖНЮЮ бумагу, и человек сверял бы
   *  форму не с тем документом. Хуже того: PDF, заменивший картинку, рисовался бы как картинка. */
  blobPath: string | null;
  fileName: string | null;
}) {
  const { key, blob, mimeType, failed } = useScanBytes(invoiceId, blobPath);
  const kinds = useFileKinds();
  // PDF по имени — ядро просмотрщика грузится вместе с файлом, а не после него: пока байтов нет,
  // просмотрщик вида не знает и сам кусок pdf.js не тянет.
  const likelyPdf = nameShownAs(kinds.data, fileName) === 'pdf';
  useEffect(() => { if (likelyPdf) preloadPdfViewer(); }, [likelyPdf]);

  if (failed) {
    return (
      <Note>
        Файл не загрузился. Он приложен к счёту — значит дело в связи или в хранилище, а не в самом
        файле: обновите страницу.
      </Note>
    );
  }

  // Реестр видов ещё в пути — ждём и его: без него про любой файл пришлось бы ответить «показать
  // нечем», и PDF на миг предлагался бы к скачиванию. Ждём, только пока запрос ИДЁТ (`isLoading`):
  // вставший на паузу без сети остаётся «ожидающим» бессрочно, и панель держала бы «загружается»
  // над файлом, который уже здесь (ревью PR #1279).
  if (!blob || kinds.isLoading) return <Note>Файл загружается…</Note>;

  // Чем показывать, решает вид, с которым файл ОТДАЛ сервер (issue #1265), а не запись в счёте:
  // сервер определяет его по содержимому и чужой вид наружу не выпускает. Что из этого показывается,
  // говорит реестр видов (issue #1266). Файл, который показать нечем, не открывается вовсе — только
  // скачивается: открытый «как есть», он выполнился бы страницей этого же сайта.
  //
  // ⚠️ Реестр не пришёл — это НЕ «показать нечем»: про файл мы тогда не знаем ничего, и сказать
  // «открываются PDF и изображения» над PDF значило бы выдать свой отказ за свойство файла.
  const shownAs = kinds.data ? fileShownAs(kinds.data, mimeType) : 'unknown';
  const canOpen = shownAs === 'pdf' || shownAs === 'image';

  return (
    <div className="h-full flex flex-col min-h-0">
      <div className="flex items-center gap-2 px-3 py-2 border-b border-outline-variant shrink-0">
        <Icon icon={Description} size={18} className="text-on-surface-variant shrink-0" />
        <span className="text-body-small text-on-surface-variant truncate flex-1">{fileName ?? 'Файл счёта'}</span>
        {canOpen
          ? (
            // Выход в полный просмотрщик браузера (поиск, печать с настройками), а не «крупнее»: крупнее
            // делает масштаб в панели (issue #1491, [UI-20]).
            <Button icon={<Icon icon={OpenInNew} size={18} />} onClick={() => void openInNewTab(blob)}>
              Открыть во вкладке
            </Button>
          )
          : (
            <Button icon={<Icon icon={Download} size={18} />}
              onClick={() => saveScan(blob, fileName, mimeType, kinds.data)}>
              Скачать
            </Button>
          )}
      </div>
      {/* Своя область наложения и обрезка: лист не выходит за неё поверх строки файла. */}
      <div className="relative isolate overflow-hidden flex-1 min-h-0 bg-surface-container-low">
        {/* PDF и картинку различает сам просмотрщик — по типу байтов, своим белым списком ([UI-24]).
            Ключ — счёт И файл: заменённый скан показывается с начала, без прежнего поворота. */}
        {canOpen && (
          <PdfViewer source={blob} documentKey={key} rememberZoomAs="invoice-panel" title={fileName ?? 'Файл счёта'}
            actions={<OriginalButton invoiceId={invoiceId} fileName={fileName} label="Оригинал" />} />
        )}
        {shownAs === 'download' && (
          <Note>
            Файл приложен, но показать его здесь нечем: рядом с формой
            открываются {wordsOf(shownKinds(kinds.data))}. Скачайте его кнопкой выше.
          </Note>
        )}
        {shownAs === 'unknown' && (
          <Note>
            <div className="flex flex-col items-center">
              <span>
                Файл загружен, но чем его показать, выяснить не удалось: список видов файлов с сервера
                не пришёл. Дело в связи, а не в файле — повторите или скачайте его кнопкой выше.
              </span>
              <Button variant="text" className="mt-2" loading={kinds.isFetching}
                icon={<Icon icon={RotateRight} size={18} />} onClick={() => void kinds.refetch()}>
                Повторить
              </Button>
            </div>
          </Note>
        )}
      </div>
    </div>
  );
}

/**
 * Замена панели на узком экране — ЧЕСТНАЯ, с названной причиной (решение владельца 28.09.2026).
 *
 * Молчаливое исчезновение панели читается как «файла нет»: человек видит форму без бумаги и не знает,
 * то ли файл не приложен, то ли система его не показывает.
 *
 * Файл открывается просмотрщиком ПОВЕРХ формы, а не вкладкой (issue #1491, D4 зонтика viewer): окно,
 * открытое после загрузки, блокировщик всплывающих окон резал молча, а на Android 14 и ниже вкладка с
 * PDF — это скачивание. Слой от жеста не зависит, поэтому байты грузятся после нажатия. Форма под
 * слоем остаётся с набранным. Для Excel и Word поверх формы встаёт вид для чтения — с оговоркой
 * строкой над листом: во вкладке её не было бы.
 */
export function ScanTooNarrow({ invoiceId, blobPath, fileName, width }: {
  invoiceId: string; blobPath: string | null; fileName: string | null; width: number;
}) {
  const [busy, setBusy] = useState(false);
  const [open, setOpen] = useState(false);
  // Что стоит в слое. Держится и после закрытия: иначе закрывающееся окно мигнуло бы «загружается».
  const [shown, setShown] = useState<{ key: string; source: Blob | null; title: string; reading: boolean } | null>(null);
  const kinds = useFileKinds();
  const toast = useToast();
  const image = useInvoiceImage(invoiceId, blobPath);
  const built = image.data?.state === 'built';
  const refused = image.data?.state === 'refused' ? image.data : null;
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

  return (
    <div className="flex items-start gap-2 rounded-md border border-outline-variant bg-surface-container px-3 py-2">
      <Icon icon={Description} size={18} className="text-on-surface-variant shrink-0 mt-0.5" />
      <div className="min-w-0 flex-1">
        {refused
          ? (
            <p className="text-body-small text-on-surface-variant">
              Файл приложен, но вид для чтения не построен. {refused.reason}
              {refused.aboutFile ? ' Показать и распознать его нельзя.' : ' Дело не в файле — откройте счёт позже.'}
            </p>
          )
          : (
            <p className="text-body-small text-on-surface-variant">
              Файл приложен, но рядом с формой он не показывается: для двух панелей нужна ширина
              не меньше 1280 пикселей, а здесь {width}.
            </p>
          )}
        {built && <p className="text-body-small text-on-surface-variant">Откроется вид для чтения — {IMAGE_CAVEAT}.</p>}
        {(built || refused) && (
          <div className="flex gap-2 mt-1">
            {built && (
              <Button variant="text" icon={<Icon icon={OpenInFull} size={18} />} onClick={openReading}>
                Открыть вид для чтения
              </Button>
            )}
            <OriginalButton invoiceId={invoiceId} fileName={fileName} label="Скачать оригинал" />
          </div>
        )}
        {!built && !refused && (
          <Button variant="text" loading={busy || kinds.isLoading || image.isLoading} icon={<Icon icon={OpenInFull} size={18} />}
            onClick={() => void openFile()}>
            Открыть файл
          </Button>
        )}
      </div>
      {shown && (
        <PdfViewerOverlay open={open} onOpenChange={setOpen} source={shown.source} documentKey={shown.key}
          title={shown.title} rememberZoomAs="invoice-overlay"
          note={shown.reading ? <span data-testid="image-caveat">Вид для чтения — {IMAGE_CAVEAT}.</span> : undefined}
          actions={<OriginalButton invoiceId={invoiceId} fileName={fileName} label="Оригинал" />} />
      )}
    </div>
  );
}

/** Приложить или заменить файл счёта. Кнопкой, а не полем формы: файл уезжает своим адресом. */
export function ScanUploadButton({ hasScan, busy, onPick }: {
  hasScan: boolean; busy: boolean; onPick: (file: File) => void;
}) {
  const input = useRef<HTMLInputElement>(null);
  // Предлагается то, что потом будет чем показать или прочитать: Excel и Word сами не
  // показываются, но стоят рядом с формой видом для чтения (issue #1270). Пока реестра нет, выбор
  // не сужается: вид всё равно определит сервер, а пустой `accept` у кнопки — меньшее зло, чем
  // кнопка, которая ждёт.
  const kinds = useFileKinds();
  const offered = [...new Set([...shownKinds(kinds.data), ...recognizedKinds(kinds.data)])];
  const accept = acceptOf(offered) || undefined;

  return (
    <>
      <Button variant="outlined" loading={busy} icon={<Icon icon={UploadFile} size={18} />}
        onClick={() => input.current?.click()}>
        {hasScan ? 'Заменить файл' : 'Приложить файл'}
      </Button>
      <input ref={input} type="file" className="hidden" accept={accept}
        onChange={e => {
          const file = e.target.files?.[0];
          // Значение сбрасываем: иначе выбор ТОГО ЖЕ файла второй раз не вызовет события, и повтор
          // после неудачной загрузки выглядел бы как «кнопка не работает».
          e.target.value = '';
          if (file) onPick(file);
        }} />
    </>
  );
}

/** Ждут дольше трёх секунд — уже не «загружается», и об этом стоит сказать. */
function useSlow(waiting: boolean) {
  const [slow, setSlow] = useState(false);
  useEffect(() => {
    if (!waiting) return;
    const timer = setTimeout(() => setSlow(true), 3000);
    return () => { clearTimeout(timer); setSlow(false); };
  }, [waiting]);
  return waiting && slow;
}

/**
 * Файл одним чтением — байтами для просмотрщика. Объектного адреса здесь нет: его делает и отзывает
 * тот, кому он нужен (`saveScan`, `openInNewTab`, режим картинки просмотрщика).
 */
function useScanBytes(invoiceId: string, blobPath: string | null) {
  const [loaded, setLoaded] = useState<{
    key: string; blob: Blob | null; mimeType: string | null; failed: boolean;
  } | null>(null);
  const key = `${invoiceId}|${blobPath ?? ''}`;

  useEffect(() => {
    let cancelled = false;
    loadInvoiceScan(invoiceId)
      .then(({ blob, mimeType }) => { if (!cancelled) setLoaded({ key, blob, mimeType, failed: false }); })
      .catch(() => { if (!cancelled) setLoaded({ key, blob: null, mimeType: null, failed: true }); });
    return () => { cancelled = true; };
  }, [invoiceId, key]);

  // Ответ считается своим только по совпадению счёта И файла. Сбрасывать состояние в эффекте нельзя
  // (лишний кадр), а без сверки при переключении счёта или замене скана на миг показалась бы ЧУЖАЯ
  // бумага — человек решил бы, что приложено не то.
  return loaded?.key === key ? loaded : { key, blob: null, mimeType: null, failed: false };
}
