// Посев сотрудников для живых прогонов (work-01): зовёт его `seed.mjs` своими помощниками запросов.

/**
 * Сотрудники и их учётные записи (work-01, ТЗ WORK-47): справочник и двое с учётными записями того же
 * ФИО — предмет режима «Сопоставить» в `pages-smoke` и вида «только чтение» в `limited-smoke`.
 *
 * <p>«Персона» — родитель справочника, её код — код ядра (`CoreRecordTypes.PersonCode`); справочник
 * заводит адрес «Завести», без перезапуска сервера, — тем же путём, что кнопка на экране. Связей
 * посев не ставит: их ставит и снимает прогон, и повторный запуск начинает с чистого листа.</p>
 */
export const EMPLOYEES = [
  { name: 'Смоков Иван Петрович', number: 'E2E-01', email: 'smokov@bhs.local' },
  { name: 'Прогонова Анна Сергеевна', number: 'E2E-02', email: 'progonova@bhs.local' },
];

export async function ensureEmployees({ api, findType, ensureUser }) {
  if (!(await findType('Персона'))) {
    await api('POST', '/document-types', {
      name: 'Персона', code: 'Персона', kind: 'Composite', parentId: null, schema: JSON.stringify({ fields: [] }), module: 'core',
    });
    console.log('  + тип «Персона»');
  }
  const { created } = await api('POST', '/employees/directory');
  if (created) console.log('  + справочник сотрудников');
  const existing = await api('GET', '/employees');
  for (const e of EMPLOYEES) {
    await ensureUser(e.email, e.name, 'User');
    if (existing.some(x => x.displayName === e.name)) continue;
    await api('POST', '/employees', { displayName: e.name, data: JSON.stringify({ ТабельныйНомер: e.number }) });
    console.log(`  + сотрудник ${e.name}`);
  }
}
