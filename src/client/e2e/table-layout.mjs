// Раскладка таблицы данных: закреплённая колонка и плотность ([UI-78], sharedui-md3-14). Своим модулем, а не
// строками в table-view-smoke.mjs: тот стоит у порога храповика размера. Вход и посев — того прогона.
//
// Аргументы обеих проверок: `openPinned()` — посеянные счета с закреплённой первой колонкой, уже с
// дождавшимися строками; `heads()` — клетки шапки; `rows()` — строки с данными (без строк-состояний).

/** Закреплённая колонка остаётся на месте при прокрутке вбок — и шапка, и клетка строки. */
export async function pinnedColumnStaysOnScroll(page, { openPinned, heads, rows }) {
  await openPinned();
  await page.setViewportSize({ width: 900, height: 1000 });

  // И шапка, и клетка строки: шапка липкая сама по себе (она держится у верхнего края), и
  // закрепление, сломанное в строках, по одной шапке осталось бы незамеченным.
  const first = heads().first();
  const cell = rows().first().locator('td').first();
  const before = { head: (await first.boundingBox()).x, cell: (await cell.boundingBox()).x };
  const moved = await page.locator('table').evaluate(t => {
    t.parentElement.scrollLeft = 400;
    return t.parentElement.scrollLeft;
  });
  if (moved < 100) throw new Error('таблица вбок не прокручивается — проверять закрепление не на чем');
  const after = { head: (await first.boundingBox()).x, cell: (await cell.boundingBox()).x };
  if (Math.abs(after.head - before.head) > 1) throw new Error(`закреплённый заголовок уехал на ${before.head - after.head} px`);
  if (Math.abs(after.cell - before.cell) > 1) throw new Error(`закреплённая клетка уехала на ${before.cell - after.cell} px`);
  const second = (await heads().nth(1).boundingBox()).x;
  if (second >= before.head + (await first.boundingBox()).width)
    throw new Error('вторая колонка не ушла под закреплённую — прокрутки не было');
}

/**
 * Плотность и фон строки (вариант А): шапка 56, строка не ниже 48; открытая строка — secondary-container, и
 * закреплённая клетка того же фона и с тем же слоем наведения. Числа меряются в браузере, потому что задают
 * их переменные CSS — класс в разметке есть и тогда, когда переменная не дошла до клетки.
 */
export async function densityByPlace(page, { openPinned, heads, rows }) {
  await page.setViewportSize({ width: 1500, height: 1000 });
  await openPinned();
  const head = (await heads().first().boundingBox()).height;
  if (Math.round(head) !== 56) throw new Error(`шапка таблицы на странице ${head} px, а не 56`);
  const heights = await rows().evaluateAll(list => list.map(r => r.getBoundingClientRect().height));
  if (heights.some(h => h < 47.5)) throw new Error(`строка ниже 48: ${heights.join(', ')}`);
  if (!heights.some(h => Math.round(h) === 48)) throw new Error(`ни одной строки в 48 — высота не от переменной: ${heights.join(', ')}`);

  // Открываем строку щелчком по второй клетке: в первой стоит ссылка на счёт.
  const row = rows().first();
  await row.locator('td').nth(1).click();
  await page.getByRole('complementary', { name: 'Строка: счёт' }).waitFor({ timeout: 5000 });
  const roleColor = await page.evaluate(() => {
    const probe = document.body.appendChild(document.createElement('div'));
    probe.style.backgroundColor = 'var(--color-secondary-container)';
    const color = getComputedStyle(probe).backgroundColor;
    probe.remove();
    return color;
  });
  const look = () => row.evaluate(r => {
    const cell = r.querySelector('td');
    return {
      row: getComputedStyle(r).backgroundColor, cell: getComputedStyle(cell).backgroundColor,
      rowLayer: getComputedStyle(r).backgroundImage, cellLayer: getComputedStyle(cell).backgroundImage,
      sticky: getComputedStyle(cell).position,
    };
  });
  const opened = await look();
  if (opened.sticky !== 'sticky') throw new Error('первая клетка не закреплена — проверять фон закреплённой не на чем');
  if (opened.row !== roleColor) throw new Error(`открытая строка ${opened.row}, а не secondary-container ${roleColor}`);
  if (opened.cell !== opened.row) throw new Error(`закреплённая клетка открытой строки ${opened.cell}, строка — ${opened.row}`);

  // Наведение — слоем состояния строки, и он же на закреплённой клетке (а не только на прокручиваемой части).
  await page.mouse.move(5, 5);
  await page.waitForTimeout(400);
  const rest = await look();
  await row.locator('td').first().hover({ position: { x: 40, y: 10 } });
  await page.waitForTimeout(400);
  const hovered = await look();
  if (hovered.rowLayer === rest.rowLayer) throw new Error('у строки под курсором слоя состояния нет');
  if (hovered.cellLayer !== hovered.rowLayer)
    throw new Error(`слой наведения на закреплённой клетке «${hovered.cellLayer}», у строки — «${hovered.rowLayer}»`);
}
