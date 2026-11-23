import assert from 'node:assert/strict';
import { BASE, underRoute } from './harness.mjs';

export function near(actual, expected, message) {
  assert.ok(Math.abs(actual - expected) <= 1, `${message}: ${actual} px, ожидалось ${expected} px`);
}

export async function checkFieldSpacing(header, control) {
  const { frame, label } = await control.evaluate(async element => {
    await document.fonts.ready;
    const wrapper = element.closest('div:has(> fieldset)');
    const caption = wrapper?.querySelector(':scope > label, :scope > span');
    if (!caption || getComputedStyle(caption).visibility !== 'visible') {
      throw new Error('нет видимой подписи поля');
    }
    await Promise.all(caption.getAnimations().map(animation => animation.finished));
    return {
      frame: wrapper.getBoundingClientRect().toJSON(),
      label: caption.getBoundingClientRect().toJSON(),
    };
  });
  const head = await header.boundingBox();
  assert.ok(head, 'шапка не видна');
  const bottom = head.y + head.height;
  assert.ok(label.width > 0 && label.height > 0 && label.top >= bottom,
    `подпись пересекает шапку: зазор ${label.top - bottom} px`);
  near(frame.top - bottom, 16, 'отступ первого поля от шапки');
  near(frame.height, 56, 'высота поля формы');
  return label;
}

export async function checkCardFields(page, check) {
  for (const [kind, tab, item] of [
    ['primitive', 'Примитивные', 'Цело число'],
    ['enum', 'Перечисления', 'Стадия работ'],
  ]) {
    await check(`card-field-spacing-${kind}`, async () => {
      await page.goto(`${BASE}/field-types`);
      await page.getByRole('tab', { name: tab, exact: true }).click();
      await page.getByRole('button').filter({ has: page.getByText(item, { exact: true }) }).click();
      const header = page.getByRole('heading', { name: 'Параметры', exact: true }).locator('..');
      const card = header.locator('..');
      const name = card.getByRole('textbox', { name: /^Название\*?$/ });
      const code = card.getByRole('textbox', { name: 'Код', exact: true });
      assert.ok(await name.inputValue(), 'название типа пусто');
      assert.ok(await code.inputValue(), 'код типа пуст');
      await checkFieldSpacing(header, name);
      await checkFieldSpacing(header, code);
      await name.focus();
      await checkFieldSpacing(header, name);
    });
  }
}

export async function checkCompanyCurrency(page, check) {
  const reason = 'Валюту учёта нельзя сменить: есть счета.';
  for (const locked of [false, true]) {
    await check(`company-section-spacing-${locked ? 'locked' : 'editable'}`, async () => {
      // Две ветви ответа на чтение. Документы и настройки компании не меняются.
      await underRoute(page, '**/api/settings/company/currency', async route => {
        if (route.request().method() !== 'GET') return route.continue();
        const response = await route.fetch();
        assert.ok(response.ok(), `валюта учёта: HTTP ${response.status()}`);
        await route.fulfill({
          response,
          json: { ...await response.json(), locked, stale: null, reason: locked ? reason : null },
        });
      }, async () => {
        await page.goto(`${BASE}/settings`);
        const header = page.getByRole('button', { name: /^Компания$/i });
        if (await header.getAttribute('aria-expanded') === 'false') await header.click();
        const section = header.locator('..');
        const field = section.getByRole('combobox', { name: 'Валюта учёта', exact: true });
        if (locked) {
          const text = section.getByTestId('currency-locked');
          await text.getByText(reason, { exact: true }).waitFor();
          assert.ok(await text.getByText('Валюта учёта', { exact: true }).isVisible());
          assert.equal(await field.count(), 0, 'для заблокированной валюты показали выбор');
          const head = await header.boundingBox();
          near((await text.boundingBox()).y - head.y - head.height, 16, 'отступ текста от шапки');
        } else {
          await field.waitFor();
          await checkFieldSpacing(header, field);
          await field.focus();
          assert.ok(await field.evaluate(element => element === document.activeElement));
          await checkFieldSpacing(header, field);
        }
      });
    });
  }
}
