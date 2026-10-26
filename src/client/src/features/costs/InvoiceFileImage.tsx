import * as Popover from '@radix-ui/react-popover';
import { useEffect, useState } from 'react';
import { CloudOff, Download, ExternalLink, FileSpreadsheet, FileX, Info, RotateCw } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { loadInvoiceScan } from '@/shared/api/invoices';
import { loadInvoiceImage, useRebuildInvoiceImage, type InvoiceImage } from '@/shared/api/invoiceImage';
import { useFileKinds } from '@/shared/api/fileKinds';
import { useCan } from '@/shared/api/access';
import { usePrintProfiles, type PrintProfile } from '@/shared/api/printProfiles';
import { formatDateTime } from '@/shared/format/format';
import { useToast } from '@/shared/ui/Toast';
import { saveScan } from './scanView';
import { Tooltip } from '@/shared/ui/Tooltip';
import { canChooseProfile, layoutHint, layoutNotice, rebuiltMessage } from './invoiceImageLayout';
import { LayoutChooser } from './LayoutChooser';
import { PdfViewer } from '@/shared/ui/viewer/PdfViewer';
import { openInNewTab } from '@/shared/ui/viewer/openInNewTab';
import { preloadPdfViewer } from '@/shared/ui/viewer/viewerGate';

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
          const { blob, mimeType } = await loadInvoiceScan(invoiceId);
          saveScan(blob, fileName, mimeType, kinds.data);
        } catch (e) { toast.apiError(e, 'Файл не скачан'); }
        finally { setBusy(false); }
      }}>
      {label}
    </Button>
  );
}

/**
 * Построенный вид: строка-оговорка, пометки построителя и сам PDF.
 *
 * <p>О профиле печати (issue #1456) — по месту и только когда есть что сказать: по чему разложен вид —
 * в подсказке оговорки; «профиль изменён / удалён / вид до профилей» и выбор профиля — тому, кто может
 * перестроить. Список профилей спрашивается только тогда же: читателю он ни к чему. Он закрыт правом
 * работы с файлами — без него выбора нет вовсе, а не отказ 403 на каждом счёте.</p>
 */
export function BuiltImage({ invoiceId, blobPath, fileName, image, canEdit }: {
  invoiceId: string; blobPath: string | null; fileName: string | null; image: InvoiceImage; canEdit: boolean;
}) {
  const viewKey = `${blobPath ?? ''}|${image.builtAt ?? ''}`;
  const { bytes, failed } = useImageBytes(invoiceId, viewKey);
  const rebuild = useRebuildInvoiceImage(invoiceId, blobPath);
  const toast = useToast();
  const mayList = useCan().permission('core.files.use');
  const asked = canEdit && mayList && image.layout === 'profile';
  const { data: profiles = NO_PROFILES, isPending: profilesPending } = usePrintProfiles({ enabled: asked });
  const notice = canEdit ? layoutNotice(image) : null;
  const hint = layoutHint(image);
  const chip = canEdit && canChooseProfile(image, profiles);
  // Пока показан чип, состояние профиля и пометка о раскладке — у него (issue #1495): раскладка иначе
  // названа трижды, а узкая колонка платит за каждую строку высотой листа. Пока профили не пришли, чип
  // ли будет, не известно — и строк нет: иначе они появлялись бы на миг и уходили в чип, дёргая лист.
  const own = chip || (asked && profilesPending) ? [] : [
    { kind: 'layout-notice', text: notice },
    { kind: 'layout-note', text: image.layoutNote },
  ];
  const lines = [...own, ...image.notes.map(text => ({ kind: undefined, text }))]
    .filter((l): l is { kind: string | undefined; text: string } => !!l.text);

  /** Перестроить; `true` — вид построен (окно выбора тогда закрывается). */
  const rebuildWith = (profileId?: string) => rebuild.mutateAsync(profileId)
    .then(next => {
      if (next.state === 'refused') { toast.info(`Вид не перестроен. ${next.reason ?? ''} Показан прежний.`); return false; }
      toast.success(rebuiltMessage(next, image));
      return true;
    })
    .catch(e => { toast.apiError(e, 'Вид не перестроен'); return false; });

  return (
    <div className="h-full flex flex-col min-h-0">
      <div className="flex items-center gap-2 px-3 py-2 border-b border-stroke shrink-0">
        <FileSpreadsheet size={14} className="text-fg3 shrink-0" />
        <span className="text-xs text-fg2 truncate flex-1">{fileName ?? 'Файл счёта'}</span>
        {/* Во вкладке — полный просмотрщик браузера: поиск, печать с настройками (issue #1490). */}
        <Button size="sm" icon={<ExternalLink size={13} />} disabled={!bytes}
          onClick={() => { if (bytes) void openInNewTab(bytes); }}>
          Открыть во вкладке
        </Button>
        <OriginalButton invoiceId={invoiceId} fileName={fileName} label="Оригинал" />
      </div>
      {/* Одна строка (issue #1495, вариант Б): короткая фраза, чип и «Перестроить»; в узкой колонке чип с
          кнопкой уходят на вторую строку вместе. Фраза не обрезается — многоточие срезало бы как раз
          «документ — оригинал»; остальное, что о виде сказать, — в её подсказке и окне. */}
      <div className="px-3 py-1.5 border-b border-stroke bg-muted text-xs text-fg2 shrink-0" data-testid="image-caveat">
        <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
          <CaveatPhrase about={`Вид для чтения${image.pages ? `, ${image.pages} стр.` : ''}: ${IMAGE_CAVEAT}. `
            + `Построен ${image.builtAt ? formatDateTime(image.builtAt) : ''} из файла${fileName ? ` «${fileName}»` : ''}. `
            + 'Текст и числа взяты из файла; расположение, шрифты и разбивка на страницы — приближённые.'
            + (hint ? ` ${hint}` : '')} />
          {canEdit && (
            <span className="flex items-center gap-2 min-w-0 max-w-full">
              {chip && (
                <LayoutChooser image={image} notice={notice} profiles={profiles} rebuilding={rebuild.isPending}
                  onRebuild={rebuildWith} />
              )}
              <Button size="sm" variant="text" loading={rebuild.isPending} icon={<RotateCw size={12} />}
                className="shrink-0" title="Построить вид заново. Сам файл и распознанное остаются как были."
                onClick={() => void rebuildWith()}>
                {rebuild.isPending ? 'Перестраиваем…' : 'Перестроить'}
              </Button>
            </span>
          )}
        </div>
        {lines.length > 0 && (
          <ul className="mt-1 space-y-0.5 text-fg2">
            {lines.map((line, i) => (
              // Не предупреждение: вид не испорчен, он лишь по прежним параметрам.
              <li key={`${i}|${line.text}`} className="flex items-start gap-1.5" data-testid={line.kind}>
                <Info size={12} aria-hidden className="shrink-0 mt-0.5 text-fg3" />{line.text}
              </li>
            ))}
          </ul>
        )}
      </div>
      {/* Своя область наложения и обрезка: лист не выходит за неё поверх строки-оговорки (issue #1490). */}
      <div className="relative isolate overflow-hidden flex-1 min-h-0 bg-base">
        {failed
          ? (
            // Отказ ДО документа — связь или хранилище, а не PDF: это говорит место, а не просмотрщик.
            <Centered>
              Вид для чтения не загрузился. Он построен — значит дело в связи или в хранилище: обновите
              страницу. Сам файл скачивается кнопкой «Оригинал».
            </Centered>
          )
          : (
            <PdfViewer source={bytes} documentKey={`${invoiceId}|${viewKey}`} rememberZoomAs="invoice-panel"
              title={`${fileName ?? 'Файл счёта'} — вид для чтения`}
              actions={<OriginalButton invoiceId={invoiceId} fileName={fileName} label="Оригинал" />} />
          )}
      </div>
    </div>
  );
}

/**
 * Фраза «Вид для чтения · документ — оригинал»: остальное о виде («вёрстка может отличаться», страницы,
 * когда и из чего построен, раскладка) — в подсказке при наведении и фокусе, а касанием и Enter — в окне:
 * подсказка на касание не открывается, а без этого текста на планшете не узнать, что вёрстка приближённая.
 */
function CaveatPhrase({ about }: { about: string }) {
  return (
    <Popover.Root>
      <Tooltip content={about}>
        <Popover.Trigger asChild>
          <button type="button" data-testid="image-caveat-phrase"
            className="flex-auto text-left rounded-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand">
            <span className="font-medium text-fg1">Вид для чтения</span> · документ — оригинал
          </button>
        </Popover.Trigger>
      </Tooltip>
      <Popover.Portal>
        <Popover.Content align="start" sideOffset={6} aria-label="Вид для чтения" data-testid="image-caveat-about"
          className="z-50 w-72 max-w-(--radix-popover-content-available-width) rounded-lg border border-stroke bg-surface p-3 text-xs text-fg1"
          style={{ boxShadow: 'var(--f-shadow16)' }}>
          {about}
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}

/**
 * Вид не построен. Файл при этом ПРИЛОЖЕН и цел — это сказано прямо: отказ построения не отказ
 * загрузки, и человек не должен прикладывать файл второй раз.
 *
 * <p>Два разных отказа выглядят по-разному. «Файл такой» (пароль, пуст, конвертер не открыл) —
 * сервер это запомнил, и сам собой вид не построится; выход — «Построить заново» (отказ конвертера
 * бывает и разовым) либо заменить файл. «Дело не в файле» (конвертер занят, молчит, не настроен) —
 * не запомнено, и «Повторить» спрашивает ещё раз.</p>
 */
export function RefusedImage({ invoiceId, blobPath, fileName, image, canEdit, retrying, onRetry }: {
  invoiceId: string; blobPath: string | null; fileName: string | null; image: InvoiceImage;
  canEdit: boolean; retrying: boolean; onRetry: () => void;
}) {
  const rebuild = useRebuildInvoiceImage(invoiceId, blobPath);
  const toast = useToast();
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
            {image.aboutFile
              ? <FileX size={20} className="text-warning" />
              : <CloudOff size={20} className="text-fg3" />}
            <span className="text-sm font-medium text-fg1">
              {image.aboutFile ? 'Вид для чтения не построен' : 'Вид для чтения пока не построен'}
            </span>
            <span>{image.reason}</span>
            <span>
              {image.aboutFile
                ? 'Показать и распознать его нельзя. Сам файл приложен и цел.'
                : 'Дело не в файле — он приложен и цел.'}
            </span>
            <div className="flex gap-2 mt-1">
              {!image.aboutFile && (
                <Button size="sm" variant="outlined" loading={retrying} icon={<RotateCw size={13} />} onClick={onRetry}>
                  Повторить
                </Button>
              )}
              {/* Запомненный отказ сам не пройдёт — а конвертер мог споткнуться и разово. */}
              {image.aboutFile && canEdit && (
                <Button size="sm" variant="outlined" loading={rebuild.isPending} icon={<RotateCw size={13} />}
                  onClick={() => rebuild.mutateAsync()
                    .then(next => { if (next.state === 'refused') toast.info(`Вид снова не построен. ${next.reason ?? ''}`); })
                    .catch(e => toast.apiError(e, 'Вид не построен'))}>
                  Построить заново
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

const NO_PROFILES: PrintProfile[] = [];

/** Сообщение посреди панели. Одно на панель файла и на вид для чтения. */
export function Centered({ children }: { children: React.ReactNode }) {
  return <div className="h-full grid place-items-center p-4 text-xs text-fg3 text-center">{children}</div>;
}

/** Вид одним чтением — байтами для просмотрщика; перечитывается, когда вид перестроен. */
function useImageBytes(invoiceId: string, key: string) {
  const [loaded, setLoaded] = useState<{ key: string; bytes: Blob | null; failed: boolean } | null>(null);
  const full = `${invoiceId}|${key}`;

  useEffect(() => {
    let cancelled = false;
    // Вид — всегда PDF: кусок просмотрщика грузится вместе с байтами, а не после них.
    preloadPdfViewer();
    loadInvoiceImage(invoiceId)
      .then(bytes => { if (!cancelled) setLoaded({ key: full, bytes, failed: false }); })
      .catch(() => { if (!cancelled) setLoaded({ key: full, bytes: null, failed: true }); });
    return () => { cancelled = true; };
  }, [invoiceId, full]);

  // Чужой ответ не показываем: при замене файла или перестроении на миг встал бы прежний вид.
  return loaded?.key === full ? loaded : { bytes: null, failed: false };
}
