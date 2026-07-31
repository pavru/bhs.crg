import { useState } from 'react';
import { Image as ImageIcon, Trash2, ChevronDown, ChevronRight } from 'lucide-react';
import { formatBytes } from '@/shared/api/attachments';
import type { ImageValue } from '@/shared/api/schema';

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
  value: unknown; onChange: (val: ImageValue | null) => void;
}) {
  const [sizeOpen, setSizeOpen] = useState(false);
  const [error, setError] = useState('');
  const img = normalize(value);
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

    const reader = new FileReader();
    reader.onerror = () => setError(`Не удалось прочитать файл «${file.name}».`);
    reader.onload = () => {
      const checked = checkImageResult(reader.result, file.name);
      if ('error' in checked) { setError(checked.error); return; }
      onChange({ ...(img ?? {}), src: checked.src });
    };
    reader.readAsDataURL(file);
  }

  const patch = (p: Partial<ImageValue>) => {
    if (!img) return;
    const next: ImageValue = { ...img, ...p };
    // Пустые строки в опциях убираем, чтобы значение не тащило пустышки.
    (['width', 'height', 'align', 'fit'] as const).forEach(k => { if (!next[k]) delete next[k]; });
    onChange(next);
  };

  if (!img) {
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
      <div className="border border-stroke rounded-lg overflow-hidden bg-base flex items-center justify-center p-2 max-h-52">
        <img src={img.src} alt="" className="max-h-48 max-w-full object-contain" />
      </div>

      <div className="flex items-center gap-4">
        <button type="button" onClick={() => onChange(null)}
          className="flex items-center gap-1.5 text-xs text-danger hover:text-danger transition-colors">
          <Trash2 size={12} /> Удалить изображение
        </button>
        <button type="button" onClick={() => setSizeOpen(o => !o)}
          className="flex items-center gap-1 text-xs text-fg3 hover:text-fg1 transition-colors">
          {sizeOpen ? <ChevronDown size={12} /> : <ChevronRight size={12} />}
          Размер и выравнивание{!sizeOpen && hasSize ? ' ·' : ''}
        </button>
      </div>

      {sizeOpen && (
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

/** Приводит значение к объекту {src, ...} или null. Понимает легаси-строку data-URI. */
function normalize(value: unknown): ImageValue | null {
  if (typeof value === 'string') return value.startsWith('data:image') ? { src: value } : null;
  if (value && typeof value === 'object') {
    const src = (value as { src?: unknown }).src;
    if (typeof src === 'string' && src.startsWith('data:image')) return value as ImageValue;
  }
  return null;
}
