import { useEffect, useRef, useState } from 'react';
import { Download, FileText, FileUp, Maximize2, RotateCw } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { loadInvoiceScan } from '@/shared/api/invoices';
import { loadInvoiceImage, useInvoiceImage } from '@/shared/api/invoiceImage';
import { acceptOf, fileShownAs, recognizedKinds, shownKinds, useFileKinds, wordsOf } from '@/shared/api/fileKinds';
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
            <Button size="sm" variant="outlined" loading={image.isFetching} icon={<RotateCw size={13} />}
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
  const { url, mimeType, failed } = useScan(invoiceId, blobPath);
  const kinds = useFileKinds();

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
  if (!url || kinds.isLoading) return <Note>Файл загружается…</Note>;

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
      <div className="flex items-center gap-2 px-3 py-2 border-b border-stroke shrink-0">
        <FileText size={14} className="text-fg3 shrink-0" />
        <span className="text-xs text-fg2 truncate flex-1">{fileName ?? 'Файл счёта'}</span>
        {canOpen
          ? (
            <Button size="sm" icon={<Maximize2 size={13} />} onClick={() => window.open(url, '_blank')}>
              Во весь экран
            </Button>
          )
          : (
            <Button size="sm" icon={<Download size={13} />}
              onClick={() => saveScan(url, fileName, mimeType, kinds.data)}>
              Скачать
            </Button>
          )}
      </div>
      <div className="flex-1 min-h-0 bg-base">
        {shownAs === 'pdf' && (
          <iframe src={url} title={fileName ?? 'Файл счёта'} className="w-full h-full border-0 bg-white" />
        )}
        {shownAs === 'image' && (
          <img src={url} alt={fileName ?? 'Файл счёта'} className="w-full h-full object-contain" />
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
              <Button size="sm" variant="text" className="mt-2" loading={kinds.isFetching}
                icon={<RotateCw size={13} />} onClick={() => void kinds.refetch()}>
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
 * Для Excel и Word окном открывается вид для чтения — с той же оговоркой, что и в панели: во вкладке
 * браузера строки-оговорки уже не будет, поэтому она сказана здесь, до нажатия.
 */
export function ScanTooNarrow({ invoiceId, blobPath, fileName, width }: {
  invoiceId: string; blobPath: string | null; fileName: string | null; width: number;
}) {
  const [busy, setBusy] = useState(false);
  const kinds = useFileKinds();
  const toast = useToast();
  const image = useInvoiceImage(invoiceId, blobPath);
  const built = image.data?.state === 'built';
  const refused = image.data?.state === 'refused' ? image.data : null;

  return (
    <div className="flex items-start gap-2 rounded-lg border border-stroke bg-surface2 px-3 py-2">
      <FileText size={14} className="text-fg3 shrink-0 mt-0.5" />
      <div className="min-w-0 flex-1">
        {refused
          ? (
            <p className="text-xs text-fg2">
              Файл приложен, но вид для чтения не построен. {refused.reason}
              {refused.aboutFile ? ' Показать и распознать его нельзя.' : ' Дело не в файле — откройте счёт позже.'}
            </p>
          )
          : (
            <p className="text-xs text-fg2">
              Файл приложен, но рядом с формой он не показывается: для двух панелей нужна ширина
              не меньше 1280 пикселей, а здесь {width}.
            </p>
          )}
        {built && <p className="text-xs text-fg3">Откроется вид для чтения — {IMAGE_CAVEAT}.</p>}
        {(built || refused) && (
          <div className="flex gap-2 mt-1">
            {built && (
              <Button size="sm" variant="text" loading={busy} icon={<Maximize2 size={13} />}
                onClick={async () => {
                  setBusy(true);
                  try {
                    const url = await loadInvoiceImage(invoiceId);
                    window.open(url, '_blank');
                    setTimeout(() => URL.revokeObjectURL(url), 60_000);
                  } catch (e) { toast.apiError(e, 'Вид для чтения не открыт'); }
                  finally { setBusy(false); }
                }}>
                Открыть вид для чтения
              </Button>
            )}
            <OriginalButton invoiceId={invoiceId} fileName={fileName} label="Скачать оригинал" />
          </div>
        )}
        {!built && !refused && <Button size="sm" variant="text" loading={busy || kinds.isLoading || image.isLoading} icon={<Maximize2 size={13} />}
          onClick={async () => {
            setBusy(true);
            // Открываем ОТДЕЛЬНЫМ окном — это и есть замена панели: бумага остаётся доступной, просто
            // не рядом.
            try {
              const { url, mimeType } = await loadInvoiceScan(invoiceId);
              // Реестра нет (не пришёл или запрос встал) — спрашиваем ещё раз, прямо сейчас.
              const info = kinds.data ?? (await kinds.refetch()).data;
              // Файл, который показать нечем, окном не открывается — скачивается (issue #1265).
              // Так же и файл, про который узнать не удалось, — но тогда причина названа: иначе PDF,
              // вдруг ушедший в загрузки вместо окна, выглядел бы как поломка кнопки.
              if (!info) {
                saveScan(url, fileName, mimeType, info);
                toast.info('Файл скачан, а не открыт: список видов файлов с сервера не пришёл, и чем его показать, неизвестно.');
              } else if (fileShownAs(info, mimeType) === 'download') saveScan(url, fileName, mimeType, info);
              else window.open(url, '_blank');
              // Отзываем с отсрочкой: окно уже открыто, но браузеру нужна живая ссылка, пока он
              // читает файл. Не отозвав вовсе, мы держали бы в памяти страницы весь скан — а рядом
              // стоит комментарий, объявляющий отзыв обязательным.
              setTimeout(() => URL.revokeObjectURL(url), 60_000);
            } finally { setBusy(false); }
          }}>
          Открыть файл в отдельном окне
        </Button>}
      </div>
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
      <Button size="sm" variant="outlined" loading={busy} icon={<FileUp size={13} />}
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
 * Файл одним чтением, с освобождением объектной ссылки.
 *
 * ⚠️ `URL.revokeObjectURL` в уборке обязателен: без него каждое открытие счёта оставляет в памяти
 * страницы файл целиком, а замечают такое на сотом счёте у заказчика.
 */
function useScan(invoiceId: string, blobPath: string | null) {
  const [loaded, setLoaded] = useState<{
    key: string; url: string | null; mimeType: string | null; failed: boolean;
  } | null>(null);
  const key = `${invoiceId}|${blobPath ?? ''}`;

  useEffect(() => {
    let cancelled = false;
    let objectUrl: string | null = null;

    loadInvoiceScan(invoiceId)
      .then(({ url, mimeType }) => {
        objectUrl = url;
        if (cancelled) { URL.revokeObjectURL(url); return; }
        setLoaded({ key, url, mimeType, failed: false });
      })
      .catch(() => { if (!cancelled) setLoaded({ key, url: null, mimeType: null, failed: true }); });

    return () => {
      cancelled = true;
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [invoiceId, key]);

  // Ответ считается своим только по совпадению счёта И файла. Сбрасывать состояние в эффекте нельзя
  // (лишний кадр), а без сверки при переключении счёта или замене скана на миг показалась бы ЧУЖАЯ
  // бумага — человек решил бы, что приложено не то.
  return loaded?.key === key ? loaded : { url: null, mimeType: null, failed: false };
}
