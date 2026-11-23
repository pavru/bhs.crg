// Посев назначений для живого прогона `work-assignments-smoke` (work-12): зовёт его `seed.mjs` своими помощниками.

/**
 * Пользователи и сотрудники прогона назначений (change `work-12-assignments-ui`, ТЗ WORK-21, WORK-46).
 *
 * - «Менеджер проекта» посева (`pm@bhs.local`, его же заводит посев смет) — сотрудник, связанный со своей
 *   учётной записью и назначенный МЕНЕДЖЕРОМ стройки посева без срока: он назначает на неё.
 * - Второй «Менеджер проекта» (`pm2@bhs.local`) — без назначения на этой стройке: право назначать у него есть, а
 *   кнопок на чужой стройке быть не должно.
 * - «Монтажник» (`montazh@bhs.local`) — пункта «Назначения» в карточке стройки у него нет.
 * - Два сотрудника-кандидата без учётных записей: их прогон назначает пачкой и уборкой снимает.
 *
 * Модуль `work` выключен — посев молчит вслух и возвращает `false`: прогон назначений на таком стенде
 * отказывает с названной причиной, а не проходит пустым.
 */
export const ASSIGNMENT_PM = { email: process.env.SMOKE_PM_EMAIL || 'pm@bhs.local', name: 'Павел Проектов' };
export const FOREIGN_PM = { email: 'pm2@bhs.local', name: 'Мария Чужова' };
export const INSTALLER = { email: 'montazh@bhs.local', name: 'Игорь Монтажов' };
export const ENTRY_EMPLOYEE = { name: 'Никитин Виктор (ввод отчёта)', number: 'E2E-23' };
export const CANDIDATES = [
  { name: 'Белов Андрей Сергеевич (посев)', number: 'E2E-11' },
  { name: 'Лаптев Денис Олегович (посев)', number: 'E2E-12' },
];

export async function seedAssignments({ api, ensureUser, constructionId }) {
  // Модуль выключен — 501 ворот модуля, и только он; прочий отказ — отказ посева.
  const site = await api('GET', `/work/assignments?constructionId=${constructionId}`).catch(e => {
    if (!/ → 501 /.test(e.message)) throw e;
    console.log(`  ! назначения не сеются: модуль work выключен (${e.message})`);
    return null;
  });
  if (site === null) return false;

  await ensureUser(ASSIGNMENT_PM.email, ASSIGNMENT_PM.name, 'ProjectManager');
  await ensureUser(FOREIGN_PM.email, FOREIGN_PM.name, 'ProjectManager');
  await ensureUser(INSTALLER.email, INSTALLER.name, 'Installer');

  const employees = await api('GET', '/employees');
  const ensureEmployee = async (name, number) => {
    const found = employees.find(e => e.displayName === name);
    if (found) return found;
    const made = await api('POST', '/employees', { displayName: name, data: JSON.stringify({ ТабельныйНомер: number }) });
    console.log(`  + сотрудник ${name}`);
    return made;
  };

  // Менеджер назначается только сотруднику со связанной учётной записью.
  const users = await api('GET', '/users');
  const pmUser = (Array.isArray(users) ? users : users.items ?? [])
    .find(u => (u.email ?? '').toLowerCase() === ASSIGNMENT_PM.email.toLowerCase());
  const pm = await ensureEmployee(ASSIGNMENT_PM.name, 'E2E-10');
  if (pm.account?.state !== 'linked') {
    await api('PUT', `/employees/${pm.id}/account`, { userId: pmUser.id }, { 'If-Match': pm.version });
    console.log(`  + связь ${ASSIGNMENT_PM.name} → ${ASSIGNMENT_PM.email}`);
  }
  for (const c of CANDIDATES) await ensureEmployee(c.name, c.number);

  const installer = await ensureEmployee(INSTALLER.name, 'E2E-13');
  const installerUser = (Array.isArray(users) ? users : users.items ?? [])
    .find(u => (u.email ?? '').toLowerCase() === INSTALLER.email.toLowerCase());
  if (installer.account?.state !== 'linked')
    await api('PUT', `/employees/${installer.id}/account`, { userId: installerUser.id }, { 'If-Match': installer.version });
  const entryEmployee = await ensureEmployee(ENTRY_EMPLOYEE.name, ENTRY_EMPLOYEE.number);
  // Оба назначения покрывают вчера и сегодня по стройке независимо от момента запуска CI.
  const today = new Date(`${site.construction.today}T00:00:00Z`);
  const dayAt = days => new Date(today.getTime() + days * 86_400_000).toISOString().slice(0, 10);
  for (const employee of [installer, entryEmployee]) {
    if (site.items.some(a => a.employeeId === employee.id && a.kind === 'installer'
      && a.dateFrom <= dayAt(-1) && a.dateTo >= dayAt(0))) continue;
    await api('POST', '/work/assignments', {
      employeeId: employee.id, constructionId, kind: 'installer', dateFrom: dayAt(-1), dateTo: dayAt(7),
    });
  }

  if (!site.items.some(a => a.employeeId === pm.id && a.kind === 'manager')) {
    await api('POST', '/work/assignments', { employeeId: pm.id, constructionId, kind: 'manager', dateFrom: '2026-01-01', dateTo: null });
    console.log(`  + ${ASSIGNMENT_PM.name} — менеджер стройки посева`);
  }
  return true;
}
