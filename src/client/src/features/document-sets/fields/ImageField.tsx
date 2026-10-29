import { useCallback, useEffect, useRef, useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { CircularProgress } from '@/shared/ui/CircularProgress';
import { ChevronRight, Delete, ImageIcon, KeyboardArrowDown } from '@/shared/ui/icons';
import { formatBytes, uploadImage, loadAttachmentObjectUrl } from '@/shared/api/attachments';
import { beginUpload, endUpload } from '@/shared/ui/uploadsInFlight';
import { apiError } from '@/shared/utils/apiError';
import type { ImageValue, ImageBlobValue, ImageOptions } from '@/shared/api/schema';
import { checkImageResult } from './checkImageResult';
import { Select, SelectItem } from '@/shared/ui/Select';
import { Tooltip } from '@/shared/ui/Tooltip';

/**
 * Предел проверяет СЕРВЕР (issue #534). Клиентская константа была осознанной временной мерой: у
 * поля не было своего эндпоинта, и дублировать было нечего. Теперь он есть — `POST /attachments/image`
 * со своим `UploadLimits.Attachment`, — и вторая истина здесь стала ровно тем дефектом, ради которого
 * лимиты сводили в #482. Хуже того, предел в 10 МБ просил человека уменьшить картинку руками ровно
 * там, где сервер теперь уменьшает её сам.
 */

/**
 * Поле-изображение. Значение — объект `{ src: data-URI, width?, height?, align?, fit? }` (issue #246):
 * размер/выравнивание задаются здесь, в инстансе (раньше — в определении типа). Легаси-значение
 * (голая data-URI строка) читается как `{ src }` без размера.
 */
export function ImageField({ value, onChange, readOnly = false }: {
  value: unknown; onChange: (val: ImageValue | ImageBlobValue | null) => void;
  /**
   * Картинку кладёт не человек (привязка к источнику, замок модуля — issue #958): показываем её,
   * но не даём ни выбрать, ни удалить, ни менять размер. Выбор и удаление убираем СОВСЕМ, а не
   * отключаем: отключённая кнопка «Удалить изображение» обещает действие, которого не будет.
   */
  readOnly?: boolean;
}) {
  const [sizeOpen, setSizeOpen] = useState(false);
  const [error, setError] = useState('');
  /** Итог уменьшения — показываем ОДИН раз после загрузки: человеку важен вес, а не пиксели. */
  const [shrunk, setShrunk] = useState<string | null>(null);
  /** Локальное превью выбранного файла — показывается СРАЗУ, пока байты едут в хранилище. */
  const [pending, setPending] = useState<string | null>(null);
  /**
   * Номер актуального выбора (issue #532). Загрузка асинхронна, а поле за это время могут очистить
   * или выбрать другой файл: без маркера удалённая картинка «воскресала» бы, когда доедет запоздавший
   * ответ, а из двух загрузок побеждала бы та, что ответила последней, — не та, что выбрана последней.
   */
  const pick = useRef(0);
  const img = normalize(value);
  // Превью держим ровно до того мига, как картинка приехала из хранилища. Сообщает об этом сам
  // загрузчик (issue #858) — раньше момент вычислялся эффектом-наблюдателем «показанное разошлось
  // с превью», то есть по следствию вместо события.
  const dropPending = useCallback(() => setPending(null), []);
  const shownSrc = useImageSrc(img, pending, dropPending);
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
    setShrunk(null);
    const mine = ++pick.current;

    // Счётчик занятости включаем с МОМЕНТА ВЫБОРА, а не после чтения (issue #532): чтение файла на
    // 10 МБ занимает заметное время, и до его конца кнопка сохранения оставалась бы активной при
    // ещё пустом значении.
    beginUpload();

    // Тип проверяем по прочитанному, как и раньше (issue #519): accept — только фильтр диалога.
    // Читаем ради проверки и локального превью; в значение уходит ссылка на блоб, а не байты.
    const reader = new FileReader();
    reader.onerror = () => { endUpload(); setError(`Не удалось прочитать файл «${file.name}».`); };
    reader.onload = () => {
      const checked = checkImageResult(reader.result, file.name);
      if ('error' in checked) { endUpload(); setError(checked.error); return; }
      if (pick.current !== mine) { endUpload(); return; }   // выбор уже сменился, пока читали
      setPending(checked.src);   // видно немедленно, ещё до загрузки
      void upload(file, mine);
    };
    reader.readAsDataURL(file);
  }

  /**
   * Байты уезжают в хранилище, в значении остаётся ссылка (issue #522). Опции размера переносим со
   * старого значения — смена картинки не должна сбрасывать заданную ширину и выравнивание.
   */
  async function upload(file: File, mine: number) {
    try {
      const { sourceBytes, storedBytes, ...node } = await uploadImage(file);
      // Запоздавший ответ не должен ни воскрешать удалённую картинку, ни перебивать более свежий
      // выбор: значение меняем, только если это всё ещё ТОТ выбор (issue #532).
      if (pick.current !== mine) return;
      // Переносим ТОЛЬКО опции размера, перечисляя их поимённо (issue #534). Аннотация
      // `const opts: ImageOptions = img` ничего не снимает во время выполнения: в spread уходило всё
      // старое значение целиком, и свежий blobPath затирался прежним — замена картинки оставляла на
      // экране старую, а только что загруженная сиротела в хранилище.
      const { width, height, align, fit } = img ?? {};
      onChange({ ...node, width, height, align, fit });
      // Пишем РЕЗУЛЬТАТ, а не «уменьшено до 2400 px»: пиксели человеку ничего не говорят, вес говорит.
      // Оригинал при этом сохранён — строка сообщает о копии, а не о потере (issue #523).
      setShrunk(storedBytes < sourceBytes
        ? `Уменьшено: ${formatBytes(sourceBytes)} → ${formatBytes(storedBytes)}`
        : null);
    } catch (e) {
      if (pick.current !== mine) return;
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
    if (readOnly)
      return (
        <div className="flex items-center justify-center border border-dashed border-outline-variant rounded-lg py-6">
          <span className="text-xs text-on-surface-variant">Изображения нет</span>
        </div>
      );
    return (
      <>
        <label className="flex flex-col items-center justify-center gap-2 border-2 border-dashed border-outline rounded-lg py-6 cursor-pointer hover:border-primary hover:bg-primary-container transition-colors">
          <Icon icon={ImageIcon} size={20} className="text-on-surface-variant" />
          <span className="text-sm text-on-surface-variant">Нажмите для выбора изображения</span>
          {/* Предел называем ДО выбора файла, а не после отказа (issue #521): по образцу аватара,
              где о правиле сказано заранее («Изображение уменьшится автоматически»). Узнать об
              ограничении, уже выбрав файл, — значит проделать работу впустую. */}
          {/* О том, что крупная картинка уменьшится, говорим ДО выбора файла — как это делает
              аватар. Узнать об этом постфактум значит гадать, почему вес не тот (issue #534). */}
          <span className="text-xs text-on-surface-variant">PNG, JPG, GIF, WEBP · крупные уменьшаются</span>
          <input type="file" accept="image/*" className="hidden" onChange={handleFile} />
        </label>
        {error && <p className="text-xs text-error mt-1">{error}</p>}
      </>
    );
  }

  return (
    <div className="space-y-2">
      {/* Пока байты едут — показываем ЛОКАЛЬНОЕ превью с индикатором в углу, а не пустую рамку:
          файл уже в браузере, и ждать нечего (issue #522). */}
      <div className="relative border border-outline-variant rounded-lg overflow-hidden bg-surface-container-low flex items-center justify-center p-2 max-h-52">
        {shownSrc
          ? <img src={shownSrc} alt="" className="max-h-48 max-w-full object-contain" />
          : <span className="py-8 text-xs text-on-surface-variant">Загрузка изображения…</span>}
        {pending && (
          <Tooltip content="Загружается в хранилище">
            <span className="absolute top-1.5 right-1.5 rounded bg-surface/90 p-1">
              <CircularProgress size={18} className="text-primary" />
            </span>
          </Tooltip>
        )}
      </div>

      {!readOnly && (
      <div className="flex items-center gap-4">
        <button type="button" onClick={() => { pick.current++; setPending(null); setShrunk(null); onChange(null); }}
          className="flex items-center gap-1.5 text-xs text-error hover:text-error transition-colors">
          <Icon icon={Delete} size={18} /> Удалить изображение
        </button>
        <button type="button" onClick={() => setSizeOpen(o => !o)} disabled={!img}
          className="flex items-center gap-1 text-xs text-on-surface-variant hover:text-on-surface transition-colors">
          {sizeOpen ? <Icon icon={KeyboardArrowDown} size={18} /> : <Icon icon={ChevronRight} size={18} />}
          Размер и выравнивание{!sizeOpen && hasSize ? ' ·' : ''}
        </button>
      </div>
      )}

      {shrunk && <p className="text-caption text-on-surface-variant">{shrunk}</p>}

      {sizeOpen && img && (
        <div className="flex flex-wrap items-center gap-2 pl-1">
          <input value={img.width ?? ''} onChange={e => patch({ width: e.target.value })}
            placeholder="ширина (напр. 4cm)"
            className="w-36 border border-outline-variant rounded px-2 py-1 text-xs bg-surface focus:outline-none focus-visible:ring-1 focus-visible:ring-primary" />
          <input value={img.height ?? ''} onChange={e => patch({ height: e.target.value })}
            placeholder="высота"
            className="w-24 border border-outline-variant rounded px-2 py-1 text-xs bg-surface focus:outline-none focus-visible:ring-1 focus-visible:ring-primary" />
          <Select size="sm" className="w-auto" aria-label="Выравнивание" value={img.align ?? ''}
            onValueChange={align => patch({ align: (align || undefined) as ImageValue['align'] })} emptyItem="выравнивание">
            <SelectItem value="left">слева</SelectItem>
            <SelectItem value="center">по центру</SelectItem>
            <SelectItem value="right">справа</SelectItem>
          </Select>
          <Select size="sm" className="w-auto" aria-label="Вписывание (fit)" value={img.fit ?? ''}
            onValueChange={fit => patch({ fit: (fit || undefined) as ImageValue['fit'] })} emptyItem="fit (вписывание)">
            <SelectItem value="contain">contain</SelectItem>
            <SelectItem value="cover">cover</SelectItem>
            <SelectItem value="stretch">stretch</SelectItem>
          </Select>
        </div>
      )}
    </div>
  );
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
function useImageSrc(
  value: ImageValue | ImageBlobValue | null,
  pending: string | null,
  onStored?: () => void,
): string | null {
  const blobPath = isBlobValue(value) ? value.blobPath : null;
  /**
   * Скачанное храним ВМЕСТЕ С ПУТЁМ, а не голым URL (issue #858). Тогда «эта копия относится к
   * нынешнему значению» — вопрос сравнения в рендере, а не состояния, которое надо успеть обнулить
   * эффектом. Заодно уходит вспышка чужой картинки: при смене пути прежний URL уже отозван, а
   * прежнее состояние всё ещё показывалось — до тех пор, пока не доедет новое.
   */
  const [loaded, setLoaded] = useState<{ path: string; url: string } | null>(null);

  useEffect(() => {
    if (!blobPath) return;
    let cancelled = false;
    let url: string | null = null;
    loadAttachmentObjectUrl(blobPath)
      .then(res => {
        if (cancelled) { URL.revokeObjectURL(res.url); return; }
        url = res.url;
        setLoaded({ path: blobPath, url: res.url });
        // Картинка приехала из хранилища — локальное превью больше не нужно (у печатных форм это
        // мегабайты в памяти) и уже вводило бы в заблуждение: показывает не то, что сохранено.
        onStored?.();
      })
      .catch(() => { /* путь в loaded не совпадёт с нынешним — покажем превью либо ничего */ });
    return () => { cancelled = true; if (url) URL.revokeObjectURL(url); };
  }, [blobPath, onStored]);

  const blobUrl = loaded?.path === blobPath ? loaded.url : null;

  // Порядок важен: как только картинка приехала ИЗ ХРАНИЛИЩА, показываем её, а не локальное превью.
  // Иначе поле до конца жизни показывало бы копию из памяти, и «а читается ли блоб» никто бы не
  // проверил — узнали бы при генерации документа.
  if (isBlobValue(value)) return blobUrl ?? pending;
  if (pending) return pending;
  return value?.src ?? null;
}
