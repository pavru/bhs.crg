import { describe, it, expect, vi, afterEach } from 'vitest';
import { settle, CAPTURE_OPTIONS, SETTLE_FRAMES, SETTLE_MS } from './screenCapture';

/**
 * Проверяем ровно то, из-за чего снимок приходил со служебным диалогом браузера (issue #906):
 * куда направлен захват и что съёмка выжидает, а не хватает первый кадр.
 *
 * Время — своё (`now`), а не системное: прогон не должен ждать по-настоящему, а `vi.useFakeTimers`
 * один здесь не помог бы — `performance.now()` внутри гонки нужен согласованным с таймерами.
 */

/** Видео-заглушка: `frames` — сколько кадров поток отдаст, остальные ожидания уйдут в таймер. */
function fakeVideo(frames: number, frameDelayMs = 10): HTMLVideoElement & { asked: number } {
  const video = {
    asked: 0,
    requestVideoFrameCallback(cb: () => void) {
      video.asked++;
      if (video.asked <= frames) setTimeout(cb, frameDelayMs);
      return video.asked;
    },
  };
  return video as unknown as HTMLVideoElement & { asked: number };
}

/** Прогон с поддельными таймерами: возвращает, сколько «прошло» времени. */
async function run(video: HTMLVideoElement): Promise<number> {
  vi.useFakeTimers();
  let clock = 0;
  const done = settle(video, () => clock);
  let settled = false;
  void done.then(() => { settled = true; });
  // Двигаем часы шагами, синхронно с таймерами, пока ожидание не кончится.
  for (let step = 0; step < 200 && !settled; step++) {
    clock += 10;
    await vi.advanceTimersByTimeAsync(10);
  }
  await done;
  return clock;
}

afterEach(() => vi.useRealTimers());

describe('CAPTURE_OPTIONS', () => {
  it('снимает вкладку, а не экран — иначе в кадр попадает служебный UI браузера', () => {
    expect(CAPTURE_OPTIONS.preferCurrentTab).toBe(true);
    expect(CAPTURE_OPTIONS.monitorTypeSurfaces).toBe('exclude');
  });
});

describe('settle', () => {
  it('ждёт положенный срок, даже когда кадры приходят сразу', async () => {
    const video = fakeVideo(SETTLE_FRAMES);
    expect(await run(video)).toBeGreaterThanOrEqual(SETTLE_MS);
  });

  it('пропускает заданное число кадров', async () => {
    const video = fakeVideo(SETTLE_FRAMES);
    await run(video);
    expect(video.asked).toBe(SETTLE_FRAMES);
  });

  it('не виснет, когда новых кадров нет вовсе', async () => {
    // Неподвижная страница может не отдать ни одного кадра: без гонки с таймером съёмка
    // повисла бы навсегда, а кнопка «Снять экран» осталась бы нажатой без ответа.
    const video = fakeVideo(0);
    const elapsed = await run(video);
    expect(elapsed).toBeGreaterThanOrEqual(SETTLE_MS);
    expect(elapsed).toBeLessThan(SETTLE_MS * 3);
  });

  it('укладывается в объявленный срок, даже когда кадры приходят под самый его конец', async () => {
    // Со свежим таймером на каждый кадр поздний кадр отодвигал бы срок, и ожидание вышло бы
    // вдвое длиннее объявленного — форма всё это время спрятана.
    const video = fakeVideo(SETTLE_FRAMES, SETTLE_MS - 10);
    expect(await run(video)).toBeLessThanOrEqual(SETTLE_MS + 20);
  });

  it('обходится без requestVideoFrameCallback — там, где его нет', async () => {
    vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => setTimeout(() => cb(0), 16));
    const video = {} as HTMLVideoElement;
    expect(await run(video)).toBeGreaterThanOrEqual(SETTLE_MS);
    vi.unstubAllGlobals();
  });

  it('не виснет без requestVideoFrameCallback, когда и кадров отрисовки нет', async () => {
    // Скрытая вкладка (свернули окно сразу после разрешения на съёмку) не получает
    // requestAnimationFrame вовсе: без гонки со сроком поток остался бы открытым, а форма с
    // набранным текстом — невидимой навсегда.
    vi.stubGlobal('requestAnimationFrame', () => 0);
    const video = {} as HTMLVideoElement;
    expect(await run(video)).toBeGreaterThanOrEqual(SETTLE_MS);
    vi.unstubAllGlobals();
  });
});
