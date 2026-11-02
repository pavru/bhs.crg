// Раскладка сетки правки ([UI-78], sharedui-md3-14 PR 2): высота строки и черта поля в клетке. Своим модулем, а не
// строками в invoice-lines-smoke.mjs и fields-smoke.mjs: прогоны и так длинные, а проверка у обоих одна.
// Числа меряются в браузере: высоту задают переменные CSS, черту — псевдоэлемент, и класса в разметке для
// них мало — он есть и тогда, когда переменная до клетки не дошла.

/**
 * Строки тела не ниже `min`: поле клетки 40 с отступом 4 px и чертой строки дают 48 (49 с чертой), у листа —
 * 40 (41). Верх — `min + 1`: строки, выросшей вдвое от старой разметки полей, проверка не пропустит.
 */
export async function rowsAtLeast(rows, min, where) {
  const heights = await rows.evaluateAll(list => list.map(r => Math.round(r.getBoundingClientRect().height * 2) / 2));
  if (heights.length === 0) throw new Error(`${where}: строк нет — мерить нечего`);
  if (heights.some(h => h < min - 0.5)) throw new Error(`${where}: строка ниже ${min}: ${heights.join(', ')}`);
  if (heights[0] > min + 1.5) throw new Error(`${where}: первая строка ${heights[0]} px, а не ${min}–${min + 1}`);
}

/**
 * Черты поля в клетке: в покое — ни одной, у фокуса — ровно одна, 2 px роли `primary`; рамки у поля нет.
 * Черта строки (нижняя граница самой клетки) — не черта поля и не считается.
 */
export async function oneLineOnFocus(field, where) {
  const lines = () => field.evaluate(el => {
    const cell = el.closest('td, th');
    const probe = document.body.appendChild(document.createElement('div'));
    probe.style.backgroundColor = 'var(--color-primary)';
    const primary = getComputedStyle(probe).backgroundColor;
    probe.remove();
    const visible = c => c !== 'transparent' && !/rgba\(.*,\s*0\)$/.test(c);
    const marks = [...cell.querySelectorAll('*')].flatMap(node => {
      const after = getComputedStyle(node, '::after');
      const own = getComputedStyle(node);
      const out = [];
      if (after.content !== 'none' && parseFloat(after.height) > 0 && visible(after.backgroundColor))
        out.push({ kind: 'черта', height: parseFloat(after.height), color: after.backgroundColor });
      if (['Top', 'Right', 'Bottom', 'Left'].some(s => parseFloat(own[`border${s}Width`]) > 0 && visible(own[`border${s}Color`])))
        out.push({ kind: 'рамка', tag: node.tagName.toLowerCase() });
      return out;
    });
    return { marks, primary };
  });
  await field.blur();
  const rest = await lines();
  if (rest.marks.length) throw new Error(`${where}: у поля в покое ${JSON.stringify(rest.marks)}`);
  await field.focus();
  const focused = await lines();
  if (focused.marks.length !== 1 || focused.marks[0].kind !== 'черта')
    throw new Error(`${where}: у поля в фокусе не одна черта: ${JSON.stringify(focused.marks)}`);
  const [line] = focused.marks;
  if (Math.round(line.height) !== 2 || line.color !== focused.primary)
    throw new Error(`${where}: черта фокуса ${line.height} px цвета ${line.color}, а не 2 px ${focused.primary}`);
}
