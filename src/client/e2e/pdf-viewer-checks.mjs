// Вид для чтения счёта на своём просмотрщике PDF (issue #1490) — живым браузером, под CSP поставки.
//
// Отдельным файлом, а не строками в `invoices-smoke`: тот у предела размера. Зовётся из его проверки
// «счёт в Excel» после того, как вид построен, — с книгой `converter-reference.xlsx`.
//
// Сверяется то, чего не видят тесты компонента с подменённым движком: worker поднялся под CSP, лист
// НАРИСОВАН (холст не белый), слой текста лёг на лист и выделяется мышью, как в браузере.

/** Оговорка вида («1 стр.») называет число листов — по ней сверяется пагинатор панели. */
async function pagesOf(caveat) {
  const m = /(\d+)\s*стр\./.exec(await caveat.innerText());
  if (!m) throw new Error(`оговорка вида не назвала число листов: «${await caveat.innerText()}»`);
  return Number(m[1]);
}

/**
 * Лист нарисован: на холсте есть тёмные точки. Пустой белый лист выглядит «показанным» и не значит
 * ничего — так выходит скан МФУ без `wasm/` pdf.js.
 */
export async function sheetDrawn(page, named, sheet) {
  const sheetHandle = await sheet.elementHandle();
  await named('первый лист не нарисован', () => page.waitForFunction(el => {
    const c = el.querySelector('canvas');
    if (!c || !c.width || !c.height) return false;
    const d = c.getContext('2d').getImageData(0, 0, c.width, c.height).data;
    for (let i = 0; i < d.length; i += 4 * 31) if (d[i] < 160 && d[i + 3] > 0) return true;
    return false;
  }, sheetHandle, { timeout: 30_000 }));
}

/**
 * Просмотрщик показал документ: область «… — вид для чтения», нарисованный первый лист, пагинатор по
 * числу листов (у одного листа его нет), выделяемый текст счёта, оговорка не наезжает на лист.
 */
export async function viewerShowsPdf(page, named, caveat) {
  const region = page.getByRole('region', { name: /вид для чтения$/ });
  await named('области вида для чтения нет', () => region.waitFor({ timeout: 30_000 }));
  const sheet = region.getByRole('group', { name: /^Страница 1 из / });
  await named('первый лист не встал в ленту', () => sheet.waitFor({ timeout: 30_000 }));
  if ((await page.getByTestId('viewer-failure').count()) !== 0)
    throw new Error(`просмотрщик отказал: «${await page.getByTestId('viewer-failure').innerText()}»`);

  await sheetDrawn(page, named, sheet);

  const pages = await pagesOf(caveat);
  const toolbar = page.getByTestId('viewer-toolbar');
  const field = toolbar.getByRole('textbox', { name: 'Номер страницы' });
  if (pages === 1) {
    if ((await field.count()) !== 0) throw new Error('у одностраничного вида на панели пагинатор');
  } else if (!(await toolbar.innerText()).includes(`из ${pages}`)) {
    throw new Error(`панель не говорит «из ${pages}»: «${await toolbar.innerText()}»`);
  }

  // Выделение мышью по слою текста даёт строку счёта — так её копируют в форму.
  const line = sheet.locator('.bhs-text-layer span', { hasText: '7700000017' }).first();
  await named('в слое текста нет ИНН поставщика', () => line.waitFor({ state: 'attached', timeout: 15_000 }));
  const box = await line.boundingBox();
  if (!box) throw new Error('строка ИНН в слое текста не имеет размеров');
  await page.mouse.move(box.x + 1, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width - 1, box.y + box.height / 2, { steps: 5 });
  await page.mouse.up();
  const selected = await page.evaluate(() => window.getSelection()?.toString() ?? '');
  if (!selected.includes('7700000017')) throw new Error(`выделение на листе дало «${selected}», а не ИНН`);
  await page.evaluate(() => window.getSelection()?.removeAllRanges());

  // Оговорка вида стоит над листом и его не закрывает.
  const [a, b] = [await caveat.boundingBox(), await region.boundingBox()];
  if (!a || !b) throw new Error('у оговорки или области вида нет размеров');
  const overlaps = a.x < b.x + b.width && b.x < a.x + a.width && a.y < b.y + b.height && b.y < a.y + a.height;
  if (overlaps) throw new Error('оговорка вида наезжает на область листа');
}
