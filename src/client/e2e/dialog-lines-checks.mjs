// Черта под шапкой и над подвалом окна — только когда тело ушло под них ([UI-75]). Наблюдатель
// пересечения в jsdom не работает, поэтому прокрутку проверяет живой браузер: окно низкое, тело
// прокручивается. Своим файлом — набор `dialogs-smoke` у порога размера (храповик, 500 строк кода).
import { until } from './harness.mjs';

/** Проверка на открытом окне `page` (последнее `[role=dialog]`); размер экрана возвращается как был. */
export async function dialogLinesFollowBodyScroll(page) {
  const size = page.viewportSize();
  const dlg = page.locator('[role=dialog]').last();
  // Высота экрана — из размеров окна, а не числом: на посеве CI тело этого окна короткое (~120),
  // и при высоте 480 оно помещалось целиком. Окно не выше 90vh; от тела оставляем видимыми 60.
  const chrome = await dlg.evaluate(d => d.getBoundingClientRect().height - d.querySelector('.overflow-y-auto').clientHeight);
  await page.setViewportSize({ width: size.width, height: Math.ceil((chrome + 60) / 0.9) });
  try {
    const lines = () => dlg.evaluate(d => {
      const body = d.querySelector('.overflow-y-auto');
      const seen = (el, side) => !!el && getComputedStyle(el)[side] !== 'rgba(0, 0, 0, 0)';
      return {
        scrollable: body.scrollHeight > body.clientHeight + 1,
        top: seen(body.previousElementSibling, 'borderBottomColor'),
        bottom: seen(body.nextElementSibling, 'borderTopColor'),
      };
    });
    const at = (where) => dlg.evaluate((d, w) => {
      const body = d.querySelector('.overflow-y-auto');
      body.scrollTop = w === 'top' ? 0 : w === 'end' ? body.scrollHeight : (body.scrollHeight - body.clientHeight) / 2;
    }, where);
    const expectLines = async (want, label) => until(async () => {
      const got = await lines();
      if (got.top !== want.top || got.bottom !== want.bottom)
        throw new Error(`${label}: черта под шапкой ${got.top}, над подвалом ${got.bottom}`);
    });

    if (!(await lines()).scrollable) throw new Error(`тело окна при высоте ${page.viewportSize().height} не прокручивается — проверять нечего`);
    await at('top');
    await expectLines({ top: false, bottom: true }, 'у верха');
    await at('middle');
    await expectLines({ top: true, bottom: true }, 'в середине');
    await at('end');
    await expectLines({ top: true, bottom: false }, 'у низа');
  } finally {
    await page.setViewportSize(size);
  }
  // Окно снова высокое: тело помещается — черт нет.
  await until(async () => {
    const both = await dlg.evaluate(d => {
      const body = d.querySelector('.overflow-y-auto');
      if (body.scrollHeight > body.clientHeight + 1) return null;
      const c = (el, side) => getComputedStyle(el)[side];
      return [c(body.previousElementSibling, 'borderBottomColor'), c(body.nextElementSibling, 'borderTopColor')];
    });
    if (both && both.some(c => c !== 'rgba(0, 0, 0, 0)')) throw new Error(`короткое тело с чертой: ${both}`);
  });
}
