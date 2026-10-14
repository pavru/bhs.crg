import type { AccessInfo, ModuleAccess } from '@/shared/api/access';
import type { UserSettings } from '@/shared/api/userSettings';
import { ruCount, ruPlural } from '@/shared/utils/pluralize';
import type { NavItem } from './navConfig';

/**
 * Рабочие пространства (ТЗ AUTH-16.1, AUTH-16.3, AUTH-16.5, OVW-18.1; issue #1001) — правила без
 * React: что за пространства есть у человека, какое открыто и что оно показывает.
 *
 * <p>Пространство — понятие КЛИЕНТА (AUTH-16.2): сужает навигацию, но не доступ. В запросы оно не
 * уходит, адреса других модулей открываются по прямой ссылке, палитра команд ищет везде. Сервер его
 * только хранит — настройками пользователя.</p>
 *
 * <p>Вынесено из компонентов нарочно, как <code>navAccess.ts</code>: это правила, которые обязан
 * проверять тест, а через отрисовку шапки он проверял бы заодно вёрстку и провайдеры.</p>
 */

/** Ключи настроек пользователя — те же, что объявил сервер (`UserSettingKeys`). */
export const OWN_KEY = 'workspaces';
export const LAST_KEY = 'workspace';
export const DEFAULT_KEY = 'workspace.default';

export const ALL = 'all';
export const ALL_NAME = 'Все доступные модули';
export const NAME_MAX = 60;

export interface OwnWorkspace {
  id: string;
  name: string;
  /** Коды модулей. Недоступные остаются: модуль включат обратно, а состав пропал бы (AUTH-16.3). */
  modules: string[];
}

export interface Workspace {
  /** Код выбора: `all`, `module:costs`, `own:<id>` — им выбор и хранится. */
  key: string;
  name: string;
  kind: 'all' | 'module' | 'own';
  modules: string[];
}

/** Модули, доступные пользователю: хотя бы одно право модуля (считает сервер, AUTH-8.1). */
export function availableModules(access: AccessInfo): ModuleAccess[] {
  return access.modules.filter(m => m.available);
}

/**
 * Переключатель есть, когда доступно больше одного модуля (AUTH-16.1). При одном сужать нечего:
 * системные пространства выродились бы в две одинаковые строки. Это обычное положение дел, не край.
 */
export function switcherShown(access: AccessInfo): boolean {
  return availableModules(access).length > 1;
}

/**
 * Системные пространства ВЫЧИСЛЯЮТСЯ из ответа доступа, а не хранятся (AUTH-16.1): хранимый список
 * стал бы вторым реестром модулей и разошёлся бы с первым при каждой правке роли.
 */
export function systemWorkspaces(access: AccessInfo): Workspace[] {
  const open = availableModules(access);
  return [
    { key: ALL, name: ALL_NAME, kind: 'all', modules: open.map(m => m.code) },
    ...open.map((m): Workspace => ({ key: `module:${m.code}`, name: m.title, kind: 'module', modules: [m.code] })),
  ];
}

/**
 * Свои пространства из настройки. Негодное значение — пустой список, а не падение шапки: сервер
 * форму проверяет, но значение могло прийти от версии, которая писала иначе.
 */
export function parseOwn(raw: string | undefined): OwnWorkspace[] {
  if (!raw) return [];
  try {
    const value: unknown = JSON.parse(raw);
    if (!Array.isArray(value)) return [];
    return value.filter((w): w is OwnWorkspace =>
      !!w && typeof w === 'object' && typeof w.id === 'string' && typeof w.name === 'string'
      && Array.isArray(w.modules) && w.modules.every((c: unknown) => typeof c === 'string'));
  } catch {
    return [];
  }
}

export function ownToWorkspace(own: OwnWorkspace): Workspace {
  return { key: `own:${own.id}`, name: own.name, kind: 'own', modules: own.modules };
}

/** Список переключателя: системные, затем свои (AUTH-16.1). */
export function workspaceList(access: AccessInfo, own: OwnWorkspace[]): Workspace[] {
  return [...systemWorkspaces(access), ...own.map(ownToWorkspace)];
}

function titleOf(access: AccessInfo, code: string): string {
  return access.modules.find(m => m.code === code)?.title ?? code;
}

/**
 * Состав пространства: доступные модули и недоступные. Недоступный в навигацию не попадает, но
 * назван вслух (AUTH-16.3) — молча выброшенный превращается в «пропал раздел». Модуль, выключенный
 * на экземпляре, в ответе доступа не значится вовсе — тогда он назван кодом: другого имени нет.
 */
export function membership(ws: Workspace, access: AccessInfo):
  { open: ModuleAccess[]; closed: { code: string; title: string }[] } {
  const open = availableModules(access).filter(m => ws.modules.includes(m.code));
  const closed = ws.modules
    .filter(code => !open.some(m => m.code === code))
    .map(code => ({ code, title: titleOf(access, code) }));
  return { open, closed };
}

/**
 * Строка состава — рядом с именем пространства в шапке и та же в окне создания (AUTH-16.1,
 * AUTH-16.5): имя само по себе не отвечает на вопрос «почему я не вижу сметы», а состав отвечает.
 */
export function compositionLabel(ws: Workspace, access: AccessInfo): string {
  const { open, closed } = membership(ws, access);
  const total = open.length + closed.length;
  const head = ruCount(total, 'модуль', 'модуля', 'модулей');
  const names = open.map(m => m.title).join(', ');
  const tail = closed.length ? `${closed.length} ${ruPlural(closed.length, 'недоступен', 'недоступны', 'недоступны')}` : '';
  return [names ? `${head}: ${names}` : head, tail].filter(Boolean).join('; ');
}

/**
 * С какого пространства начать: выбранное в этой вкладке, иначе умолчание из профиля, иначе
 * последнее выбранное, иначе «все доступные» (AUTH-16.1, AUTH-16.5; первый вход — без мастера).
 */
export function startChoice(settings: UserSettings | undefined, inTab: string | null): string {
  return inTab ?? settings?.[DEFAULT_KEY] ?? settings?.[LAST_KEY] ?? ALL;
}

export interface Resolved {
  workspace: Workspace;
  /**
   * Разовое сообщение при входе (AUTH-16.3): пространство осталось без доступных модулей и открыты
   * все разделы. Называет модуль — без имени человек не знает, о чём просить администратора.
   */
  notice: string | null;
}

/**
 * Какое пространство действует. Откат на «все доступные» — когда выбранному не осталось доступных
 * модулей; выбор при этом НЕ переписывается: модуль вернут, и пространство вступит в силу само. Так
 * же при одном доступном модуле — переключателя нет, сужать нечего, а сохранённое ждёт (AUTH-16.3).
 */
export function resolveWorkspace(access: AccessInfo, own: OwnWorkspace[], choice: string): Resolved {
  const [all] = systemWorkspaces(access);
  if (!switcherShown(access) || choice === ALL) return { workspace: all, notice: null };

  const found = workspaceList(access, own).find(w => w.key === choice);
  if (found) {
    if (membership(found, access).open.length > 0) return { workspace: found, notice: null };
    return { workspace: all, notice: fallbackNotice(found, access) };
  }
  // Системное пространство модуля, ставшего недоступным: самого пространства больше нет в списке.
  if (choice.startsWith('module:')) {
    const code = choice.slice('module:'.length);
    const gone: Workspace = { key: choice, name: titleOf(access, code), kind: 'module', modules: [code] };
    return { workspace: all, notice: fallbackNotice(gone, access) };
  }
  // Своё удалено (например, на другом компьютере) — говорить не о чем: человек сделал это сам.
  return { workspace: all, notice: null };
}

function fallbackNotice(ws: Workspace, access: AccessInfo): string {
  const missing = membership(ws, access).closed.map(m => `«${m.title}»`).join(', ');
  return `Пространство «${ws.name}» осталось без доступных модулей: нет доступа к ${missing}. Открыты все разделы.`;
}

/**
 * Входит ли пункт навигации в пространство: пункт модуля — если модуль в нём и доступен; пункт ядра
 * — если его раздел объявил хоть один доступный модуль пространства (`IAppModule.UsesSections`).
 * Пункт, не назвавший ни модуля, ни раздела, виден везде: это решение, а не забывчивость, и тест
 * требует, чтобы рабочие пункты что-то называли.
 */
export function inWorkspace(item: NavItem, ws: Workspace, access: AccessInfo): boolean {
  if (ws.kind === 'all') return true;
  const open = membership(ws, access).open;
  if (item.module) return open.some(m => m.code === item.module);
  if (item.section) return open.some(m => (m.sections ?? []).includes(item.section!));
  return true;
}

/**
 * Модуль показывается в пространстве — для страниц, где рядом пункты разных модулей (страница
 * стройки, AUTH-16.1). Доступа это не проверяет: страница спрашивает его отдельно, как и прежде.
 */
export function moduleInWorkspace(code: string, ws: Workspace, access: AccessInfo): boolean {
  return ws.kind === 'all' || membership(ws, access).open.some(m => m.code === code);
}

/**
 * Пункты в пространстве и вне его. Вторые не теряются: последней строкой навигации стоит «Ещё N
 * разделов вне пространства» (AUTH-16.1) — без неё к скрытому разделу вёл бы только поиск, то есть
 * нужно было бы заранее знать ответ.
 */
export function splitNav(items: NavItem[], ws: Workspace, access: AccessInfo): { inside: NavItem[]; outside: NavItem[] } {
  const inside: NavItem[] = [];
  const outside: NavItem[] = [];
  for (const item of items) (inWorkspace(item, ws, access) ? inside : outside).push(item);
  return { inside, outside };
}

/**
 * Пункт, которому принадлежит адрес страницы, — если он вне пространства. Тогда на странице плашка
 * «Открыто вне пространства» (AUTH-16.1): ссылка из письма открывается, а пространство не меняется.
 * Ищется самый длинный подходящий адрес: `/tables/costs.invoices/registry` точнее `/tables`.
 */
export function outsideItem(pathname: string, items: NavItem[], ws: Workspace, access: AccessInfo): NavItem | null {
  const owner = [...items]
    .sort((a, b) => b.to.length - a.to.length)
    .find(item => pathname === item.to || pathname.startsWith(`${item.to}/`));
  return owner && !inWorkspace(owner, ws, access) ? owner : null;
}

/**
 * Разделы ядра, которые придут вместе с отмеченными модулями, — строка «Вместе с ними: …» в окне
 * (AUTH-16.5). Без неё человек выберет «Счета» и решит, что механизм сломан, увидев стройки.
 * Только разделы, видные этому человеку: обещать недоступный раздел значило бы обмануть.
 */
export function coreSectionsWith(modules: string[], visibleItems: NavItem[], access: AccessInfo): string[] {
  const sections = new Set(availableModules(access)
    .filter(m => modules.includes(m.code))
    .flatMap(m => m.sections ?? []));
  return visibleItems.filter(item => !item.module && item.section && sections.has(item.section)).map(item => item.label);
}

/**
 * Почему своё пространство нельзя сохранить — причина под кнопкой, а не отказ после нажатия
 * (AUTH-16.5). null — можно.
 */
export function ownProblem(name: string, modules: string[], own: OwnWorkspace[], editingId: string | null): string | null {
  const trimmed = name.trim();
  if (!trimmed) return 'Назовите пространство';
  if (trimmed.length > NAME_MAX) return `Имя длиннее ${NAME_MAX} знаков`;
  if (trimmed.toLowerCase() === ALL_NAME.toLowerCase()) return 'Так называется системное пространство';
  if (own.some(w => w.id !== editingId && w.name.trim().toLowerCase() === trimmed.toLowerCase()))
    return 'Пространство с таким именем уже есть';
  if (modules.length === 0) return 'Отметьте хотя бы один модуль';
  return null;
}
