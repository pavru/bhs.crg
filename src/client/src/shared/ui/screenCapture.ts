/**
 * Снимок экрана средствами браузера — для формы «Сообщить об ошибке» (issue #834, #906).
 *
 * Отдельным модулем от формы: логика захвата к разметке отношения не имеет, а из файла
 * компонента её не экспортировать — правило react-refresh требует, чтобы такой файл отдавал
 * только компоненты. Без экспорта же ожидание кадров осталось бы непроверяемым, а именно оно
 * здесь и есть предмет ошибки.
 */

/**
 * Опции захвата (issue #906).
 *
 * Снимаем ВКЛАДКУ приложения, а не экран. Экран целиком приносит на снимке служебный интерфейс
 * браузера — закрывающийся диалог выбора источника и полосу «… предоставляет доступ к экрану», —
 * и ложится он ровно поверх того, о чём сообщают. Захват вкладки такого UI не содержит по
 * построению: в поток идёт содержимое страницы, без рамки окна и адресной строки.
 *
 * Кому нужно снять чужое окно, остаётся «Загрузить файл» — соседняя кнопка формы.
 *
 * Своим типом, а не приведением: `DisplayMediaStreamOptions` в lib.dom описан двумя полями
 * (audio/video), тогда как спецификация Screen Capture давно шире. Каст спрятал бы и опечатку
 * в имени свойства — браузер молча проигнорировал бы неизвестное поле.
 */
interface CaptureOptions extends DisplayMediaStreamOptions {
  /** Текущая вкладка — предвыбранный вариант в диалоге выбора источника. */
  preferCurrentTab?: boolean;
  /** Экраны в выборе не предлагать вовсе. */
  monitorTypeSurfaces?: 'include' | 'exclude';
  /** Кнопку «сменить источник» не показывать: менять не на что. */
  surfaceSwitching?: 'include' | 'exclude';
}

export const CAPTURE_OPTIONS: CaptureOptions = {
  video: true,
  audio: false,
  preferCurrentTab: true,
  monitorTypeSurfaces: 'exclude',
  surfaceSwitching: 'exclude',
};

/** Сколько кадров пропустить перед снимком и сколько минимум ждать — см. `settle`. */
export const SETTLE_FRAMES = 3;
export const SETTLE_MS = 400;

/** Есть ли в этом браузере чем снимать (http:// внутри сети — не secure context, API там нет). */
export function canCaptureScreen(): boolean {
  return typeof navigator !== 'undefined'
    && typeof navigator.mediaDevices?.getDisplayMedia === 'function';
}

/** Снимок вкладки → PNG-файл. Поток открывается и закрывается здесь же. */
export async function captureScreenshotFile(): Promise<File> {
  const stream = await navigator.mediaDevices.getDisplayMedia(CAPTURE_OPTIONS);
  try {
    return await frameToFile(stream);
  } finally {
    stream.getTracks().forEach(t => t.stop());
  }
}

/** Кадр потока → PNG-файл. */
async function frameToFile(stream: MediaStream): Promise<File> {
  const video = document.createElement('video');
  video.srcObject = stream;
  video.muted = true;
  await video.play();
  await settle(video);

  const canvas = document.createElement('canvas');
  canvas.width = video.videoWidth;
  canvas.height = video.videoHeight;
  canvas.getContext('2d')!.drawImage(video, 0, 0);
  video.pause();

  const blob = await new Promise<Blob | null>(resolve => canvas.toBlob(resolve, 'image/png'));
  if (!blob) throw new Error('Пустой снимок');
  return new File([blob], 'снимок-экрана.png', { type: 'image/png' });
}

/**
 * Даём потоку устояться перед снимком (issue #906).
 *
 * Одного кадра мало по двум причинам. Сразу после `play()` размеры видео ещё нулевые — канва
 * вышла бы пустой. И захват начинается РАНЬШЕ, чем браузер убирает с экрана свой диалог выбора
 * источника: замер показал, что первый кадр снимается через ~200 мс после старта потока, а
 * диалог к этому времени ещё виден. Захват вкладки такого UI не содержит, но выбор поверхности
 * остаётся за браузером — задержка страхует случай, когда `monitorTypeSurfaces` не поддержан.
 *
 * Ждём и кадры, и время. Одного времени мало: медленный поток не успел бы отдать ни кадра, и
 * снимок вернулся бы к той же ранней картинке. Одних кадров — тоже: у неподвижной страницы новых
 * кадров может не быть вовсе, и ожидание счётчиком повисло бы навсегда. Поэтому каждый кадр
 * гоняется наперегонки с общим сроком.
 */
export async function settle(video: HTMLVideoElement, now: () => number = () => performance.now()): Promise<void> {
  const started = now();

  if (typeof video.requestVideoFrameCallback === 'function') {
    for (let i = 0; i < SETTLE_FRAMES; i++) {
      const frame = new Promise<void>(resolve => video.requestVideoFrameCallback(() => resolve()));
      await Promise.race([frame, delay(SETTLE_MS)]);
      if (now() - started >= SETTLE_MS) break;
    }
  } else {
    await new Promise(requestAnimationFrame);
  }

  await delay(SETTLE_MS - (now() - started));
}

function delay(ms: number): Promise<void> {
  return ms > 0 ? new Promise(resolve => setTimeout(resolve, ms)) : Promise.resolve();
}
