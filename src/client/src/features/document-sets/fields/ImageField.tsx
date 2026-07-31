import { useEffect, useState } from 'react';
import { Image as ImageIcon, Trash2, ChevronDown, ChevronRight, Loader2 } from 'lucide-react';
import { formatBytes, uploadImage, loadAttachmentObjectUrl } from '@/shared/api/attachments';
import { beginUpload, endUpload } from '@/shared/ui/uploadsInFlight';
import { apiError } from '@/shared/utils/apiError';
import type { ImageValue, ImageBlobValue, ImageOptions } from '@/shared/api/schema';

/**
 * Предел размера картинки (issue #521). Значение поля лежит data-URI прямо в реквизитах, своего
 * эндпоинта у него нет — поэтому проверка клиентская, и это ТОТ САМЫЙ случай, когда дублировать
 * нечего: на сервере числа для этого поля не существует.
 *
 * Число выбрано ПО ПУТИ ЗАПРОСА, а не «на глаз» (issue #530). Считать надо не файл, а то, что
 * поедет: base64 — это ×4/3, и едет он не один, а внутри общего `PUT …/requisites` со ВСЕМИ полями
 * документа (картинки бывают и внутри массивов/составных полей). Потолок тела — 58 МБ
 * (`UploadLimits.OrdinaryRequest`). При 10 МБ на файл это 13,3 МБ закодированных, и даже четыре
 * таких поля (53,3 МБ) остаются под потолком; при 20 МБ уже три поля дают 80 МБ — Kestrel рвёт
 * запрос на транспорте, а пользователь теряет правку и не узнаёт, какое поле виновато. Ровно тот
 * дефект «заявленный предел недостижим», ради которого сводили лимиты в #482.
 *
 * Запас есть: самая крупная картинка в системе — печать на 2,8 МБ, то есть 10 МБ это 3,5 раза сверху.
 *
 * УДАЛИТЬ вместе с переездом картинок в блоб-хранилище (issue #522): там появится эндпоинт со своим
 * пределом (`UploadLimits.Attachment`), и константа мгновенно станет второй истиной.
 *
 * Уменьшать здесь ничего не будем намеренно: пока оригинал лежит в JSONB, положить его больше некуда,
 * а документы исполнительные — печать и подпись попадают в PDF, который подписывают и сдают.
 * Уменьшение появится в #523, когда станет производной от сохранённого оригинала.
 */
const MAX_IMAGE_BYTES = 10 * 1024 * 1024;

/**
 * Поле-изображение. Значение — объект `{ src: data-URI, width?, height?, align?, fit? }` (issue #246):
 * размер/выравнивание задаются здесь, в инстансе (раньше — в определении типа). Легаси-значение
 * (голая data-URI строка) читается как `{ src }` без размера.
 */
export function ImageField({ value, onChange }: {
  value: unknown; onChange: (val: ImageValue | ImageBlobValue | null) => void;
}) {
  const [sizeOpen, setSizeOpen] = useState(false);
  const [error, setError] = useState('');
  /** Локальное превью выбранного файла — показывается СРАЗУ, пока байты едут в хранилище. */
  const [pending, setPending] = useState<string | null>(null);
  const img = normalize(value);
  const shownSrc = useImageSrc(img, pending);
  // Превью держим ровно до того мига, как картинка поехала из хранилища: дальше это лишняя копия
  // в памяти (у печати — мегабайты) и повод показывать не то, что на самом деле сохранено.
  useEffect(() => {
    if (pending && shownSrc && shownSrc !== pending) setPending(null);
  }, [pending, shownSrc]);
  const hasSize = !!(img && (img.width || img.height || img.align || img.fit));

  /**
   * Прочитанное проверяем ДО записи в значение (issue #519). Раньше в него уходило что угодно:
   * `accept="image/*"` — только фильтр диалога, он обходится выбором «Все файлы», а `normalize()`
   * отбраковывал результат уже при отрисовке. Человек видел «нажал, и ничего не произошло», при этом
   * мусорная data-URI оставалась в реквизитах и уезжала в базу при сохранении.
   */
  function handleFile(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    e.target.value = '';   // сбрасываем всегда, иначе повторный выбор того же файла не сработает
    if (!file) return;
    setError('');

    // Размер проверяем ДО чтения: незачем гонять FileReader по файлу, который всё равно отвергнем.
    const tooBig = checkImageSize(file.size);
    if (tooBig) { setError(tooBig); return; }

    // Тип проверяем по прочитанному, как и раньше (issue #519): accept — только фильтр диалога.
    // Читаем ради проверки и локального превью; в значение уходит ссылка на блоб, а не байты.
    const reader = new FileReader();
    reader.onerror = () => setError(`Не удалось прочитать файл «${file.name}».`);
    reader.onload = () => {
      const checked = checkImageResult(reader.result, file.name);
      if ('error' in checked) { setError(checked.error); return; }
      setPending(checked.src);   // видно немедленно, ещё до загрузки
      void upload(file);
    };
    reader.readAsDataURL(file);
  }

  /**
   * Байты уезжают в хранилище, в значении остаётся ссылка (issue #522). Опции размера переносим со
   * старого значения — смена картинки не должна сбрасывать заданную ширину и выравнивание.
   */
  async function upload(file: File) {
    beginUpload();
    try {
      const node = await uploadImage(file);
      const opts: ImageOptions = img ?? {};
      onChange({ ...node, ...opts });
    } catch (e) {
      // Превью убираем: иначе человек уверен, что картинка на месте, а её нет (issue #522).
      setPending(null);
      setError(apiError(e, 'Не удалось загрузить изображение'));
    } finally {
      endUpload();
    }
  }

  const patch = (p: ImageOptions) => {
    if (!img) return;
    const next = { ...img, ...p };
    // Пустые строки в опциях убираем, чтобы значение не тащило пустышки.
    (['width', 'height', 'align', 'fit'] as const).forEach(k => { if (!next[k]) delete next[k]; });
    onChange(next);
  };

  if (!img && !pending) {
    return (
      <>
        <label className="flex flex-col items-center justify-center gap-2 border-2 border-dashed border-stroke-strong rounded-lg py-6 cursor-pointer hover:border-brand hover:bg-brand-subtle transition-colors">
          <ImageIcon size={20} className="text-fg4" />
          <span className="text-sm text-fg3">Нажмите для выбора изображения</span>
          {/* Предел называем ДО выбора файла, а не после отказа (issue #521): по образцу аватара,
              где о правиле сказано заранее («Изображение уменьшится автоматически»). Узнать об
              ограничении, уже выбрав файл, — значит проделать работу впустую. */}
          <span className="text-xs text-fg4">PNG, JPG, SVG, WEBP · до {limitLabel(MAX_IMAGE_BYTES)}</span>
          <input type="file" accept="image/*" className="hidden" onChange={handleFile} />
        </label>
        {error && <p className="text-xs text-danger mt-1">{error}</p>}
      </>
    );
  }

  return (
    <div className="space-y-2">
      {/* Пока байты едут — показываем ЛОКАЛЬНОЕ превью с индикатором в углу, а не пустую рамку:
          файл уже в браузере, и ждать нечего (issue #522). */}
      <div className="relative border border-stroke rounded-lg overflow-hidden bg-base flex items-center justify-center p-2 max-h-52">
        {shownSrc
          ? <img src={shownSrc} alt="" className="max-h-48 max-w-full object-contain" />
          : <span className="py-8 text-xs text-fg4">Загрузка изображения…</span>}
        {pending && (
          <span className="absolute top-1.5 right-1.5 rounded bg-surface/90 p-1" title="Загружается в хранилище">
            <Loader2 size={13} className="animate-spin text-brand" />
          </span>
        )}
      </div>

      <div className="flex items-center gap-4">
        <button type="button" onClick={() => { setPending(null); onChange(null); }}
          className="flex items-center gap-1.5 text-xs text-danger hover:text-danger transition-colors">
          <Trash2 size={12} /> Удалить изображение
        </button>
        <button type="button" onClick={() => setSizeOpen(o => !o)} disabled={!img}
          className="flex items-center gap-1 text-xs text-fg3 hover:text-fg1 transition-colors">
          {sizeOpen ? <ChevronDown size={12} /> : <ChevronRight size={12} />}
          Размер и выравнивание{!sizeOpen && hasSize ? ' ·' : ''}
        </button>
      </div>

      {sizeOpen && img && (
        <div className="flex flex-wrap items-center gap-2 pl-1">
          <input value={img.width ?? ''} onChange={e => patch({ width: e.target.value })}
            placeholder="ширина (напр. 4cm)"
            className="w-36 border border-stroke rounded px-2 py-1 text-xs bg-surface focus:outline-none focus-visible:ring-1 focus-visible:ring-brand" />
          <input value={img.height ?? ''} onChange={e => patch({ height: e.target.value })}
            placeholder="высота"
            className="w-24 border border-stroke rounded px-2 py-1 text-xs bg-surface focus:outline-none focus-visible:ring-1 focus-visible:ring-brand" />
          <select value={img.align ?? ''} onChange={e => patch({ align: (e.target.value || undefined) as ImageValue['align'] })}
            className="border border-stroke rounded px-2 py-1 text-xs bg-surface focus:outline-none focus-visible:ring-1 focus-visible:ring-brand">
            <option value="">выравнивание</option>
            <option value="left">слева</option>
            <option value="center">по центру</option>
            <option value="right">справа</option>
          </select>
          <select value={img.fit ?? ''} onChange={e => patch({ fit: (e.target.value || undefined) as ImageValue['fit'] })}
            className="border border-stroke rounded px-2 py-1 text-xs bg-surface focus:outline-none focus-visible:ring-1 focus-visible:ring-brand">
            <option value="">fit (вписывание)</option>
            <option value="contain">contain</option>
            <option value="cover">cover</option>
            <option value="stretch">stretch</option>
          </select>
        </div>
      )}
    </div>
  );
}

/**
 * Причина отказа по размеру или null (issue #521). Отказ, а не уменьшение: см. комментарий у
 * <code>MAX_IMAGE_BYTES</code>. Числа в сообщении обязательны — «файл слишком большой» не говорит
 * человеку, насколько именно и что делать.
 */
export function checkImageSize(size: number, max = MAX_IMAGE_BYTES): string | null {
  return size > max
    ? `Файл ${formatBytes(size)} — предел ${limitLabel(max)}. Уменьшите изображение и выберите снова.`
    : null;
}

/**
 * Предел пишем целыми мегабайтами («20 МБ», не «20.0 МБ») — так же, как его называет сервер в
 * `UploadLimits.TooLarge` (issue #530). Одно и то же ограничение, показанное двумя способами, читается
 * как два разных, а после переезда в блобы (#522) эти сообщения встретятся у одного поля.
 */
function limitLabel(bytes: number): string {
  const mb = bytes / (1024 * 1024);
  return Number.isInteger(mb) ? `${mb} МБ` : formatBytes(bytes);
}

/**
 * Прочитанное — картинка или причина отказа (issue #519).
 *
 * Судим по самой data-URI, а не по `file.type`: это ровно то, что потом проверит `normalize()`, и
 * два разных мерила разошлись бы. Пустой `file.type` (Windows отдаёт его, например, для `.tif`)
 * даёт `data:application/octet-stream` — такой файл поле всё равно не покажет, поэтому честнее
 * сказать об этом вслух и подсказать выход, чем молча оставить поле пустым.
 */
export function checkImageResult(result: unknown, fileName: string): { src: string } | { error: string } {
  if (typeof result !== 'string')
    return { error: `Не удалось прочитать файл «${fileName}».` };
  if (!result.startsWith('data:image'))
    return { error: `«${fileName}» — не изображение или его тип не распознан. Сохраните файл как PNG или JPG.` };
  return { src: result };
}

/**
 * Приводит значение к объекту или null. Понимает три формы: голую строку data-URI (легаси), объект
 * `{src: data-URI, …}` и ссылку на блоб `{$type:"image", blobPath, …}` (issue #522).
 *
 * Читать старые формы не перестанем никогда: восстановление бэкапа заново впрыскивает их, а архивы
 * восстановимы неограниченно долго.
 */
function normalize(value: unknown): ImageValue | ImageBlobValue | null {
  if (typeof value === 'string') return value.startsWith('data:image') ? { src: value } : null;
  if (value && typeof value === 'object') {
    const o = value as Record<string, unknown>;
    if (o['$type'] === 'image' && typeof o.blobPath === 'string' && o.blobPath) return value as ImageBlobValue;
    if (typeof o.src === 'string' && o.src.startsWith('data:image')) return value as ImageValue;
  }
  return null;
}

/** Ссылка на блоб? Форма значения после переезда (issue #522). */
function isBlobValue(v: ImageValue | ImageBlobValue | null): v is ImageBlobValue {
  return !!v && '$type' in v && v.$type === 'image';
}

/**
 * Что показать: локальное превью (пока байты едут), сама data-URI (старая форма) либо скачанный из
 * хранилища объект-URL (новая). Блоб тянем один раз на путь и освобождаем URL при смене.
 */
function useImageSrc(value: ImageValue | ImageBlobValue | null, pending: string | null): string | null {
  const blobPath = isBlobValue(value) ? value.blobPath : null;
  const [blobUrl, setBlobUrl] = useState<string | null>(null);

  useEffect(() => {
    if (!blobPath) { setBlobUrl(null); return; }
    let cancelled = false;
    let url: string | null = null;
    loadAttachmentObjectUrl(blobPath)
      .then(res => {
        if (cancelled) { URL.revokeObjectURL(res.url); return; }
        url = res.url;
        setBlobUrl(res.url);
      })
      .catch(() => { if (!cancelled) setBlobUrl(null); });
    return () => { cancelled = true; if (url) URL.revokeObjectURL(url); };
  }, [blobPath]);

  // Порядок важен: как только картинка приехала ИЗ ХРАНИЛИЩА, показываем её, а не локальное превью.
  // Иначе поле до конца жизни показывало бы копию из памяти, и «а читается ли блоб» никто бы не
  // проверил — узнали бы при генерации документа.
  if (isBlobValue(value)) return blobUrl ?? pending;
  if (pending) return pending;
  return value?.src ?? null;
}
