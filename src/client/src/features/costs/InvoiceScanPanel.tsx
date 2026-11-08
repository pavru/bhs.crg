import { useEffect, useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Description, Download, OpenInNew, RotateRight } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';
import { loadInvoiceScan } from '@/shared/api/invoices';
import { useInvoiceImage } from '@/shared/api/invoiceImage';
import { openInNewTab } from '@/shared/ui/viewer/openInNewTab';
import { PdfViewer } from '@/shared/ui/viewer/PdfViewer';
import { preloadPdfViewer } from '@/shared/ui/viewer/viewerGate';
import { fileShownAs, nameShownAs, shownKinds, useFileKinds, wordsOf } from '@/shared/api/fileKinds';
import { useCan } from '@/shared/api/access';
import { BuiltImage, Centered as Note, OriginalButton, RefusedImage } from './InvoiceFileImage';
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
      <div className="relative isolate overflow-hidden flex-1 min-h-0 bg-surface-container">
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
