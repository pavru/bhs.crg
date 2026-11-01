// Окно на телефоне — full-screen dialog ([UI-75], sharedui-md3-13 PR 1б): крестик у ведущего края, главное
// действие кнопкой в шапке, «Отмены» и подвала нет. Раскладку шапки решает JS по ширине экрана, а не
// классы, — и что кнопка, перенесённая в шапку, по-прежнему отправляет форму окна (`form=` у кнопки вне
// формы), видно только в браузере. Окно `fullScreen` на столе проверяет `allocation-matrix-smoke`:
// «Применить» и «Сохранить разноску» там нажимаются в шапке. Своим файлом — набор `dialogs-smoke` у порога
// размера (храповик, 500 строк кода).
import { BASE, settled, until } from './harness.mjs';

/** Переименовать стройку из шапки на 375 px и вернуть имя; размер экрана возвращается как был. */
export async function phoneDialogSavesFromHeader(page, construction) {
  const size = page.viewportSize();
  const dialog = page.getByRole('dialog', { name: 'Переименовать стройку' });
  await page.setViewportSize({ width: 375, height: 812 });
  try {
    await page.goto(`${BASE}/document-sets/${construction}`);
    await settled(page);
    const rename = async (to) => {
      await page.getByRole('button', { name: 'Действия стройки' }).click();
      await page.getByRole('menuitem', { name: 'Переименовать' }).click();
      await dialog.waitFor();
      const field = dialog.getByLabel('Название стройки');
      const was = await field.inputValue();
      await field.fill(to(was));
      return was;
    };
    const was = await rename(name => `${name} ·`);

    const box = await dialog.boundingBox();
    if (!box || box.x > 0 || box.y > 0 || box.width < 375 || box.height < 812)
      throw new Error(`окно на 375 px не во весь экран: ${JSON.stringify(box)}`);
    const close = await dialog.getByRole('button', { name: 'Закрыть', exact: true }).boundingBox();
    const save = await dialog.getByRole('button', { name: 'Сохранить', exact: true }).boundingBox();
    if (!close || close.x > 16 || close.y > 16) throw new Error(`крестик не у ведущего края шапки: ${JSON.stringify(close)}`);
    if (!save || save.y > 16 || save.x + save.width < 375 - 16)
      throw new Error(`«Сохранить» не у замыкающего края шапки: ${JSON.stringify(save)}`);
    if (await dialog.getByRole('button', { name: 'Отмена' }).count()) throw new Error('у окна во весь экран осталась «Отмена»');

    await dialog.getByRole('button', { name: 'Сохранить', exact: true }).click();
    await dialog.waitFor({ state: 'detached' });
    await until(async () => {
      if (!(await page.getByText(`${was} ·`).first().isVisible())) throw new Error('новое имя стройки не показано');
    });
    await rename(() => was);
    await dialog.getByRole('button', { name: 'Сохранить', exact: true }).click();
    await dialog.waitFor({ state: 'detached' });
  } finally {
    await page.setViewportSize(size);
  }
}
