import type { editor } from 'monaco-editor';

/**
 * Тема редактора кода `bhs` — из ролей палитры приложения, одна на все редакторы и обе темы (макет А
 * md3-16, [UI-80]). Встроенные `vs` и `vs-dark` — только база: свои цвета у них не из палитры.
 *
 * Значения читаются из вычисленных `--md-sys-color-*`, а не из таблицы рядом с палитрой: перегенерация
 * темы (D4 эпика) иначе развела бы редактор и приложение. Модуль не импортирует Monaco — его можно
 * проверять в jsdom; Monaco отдаёт ему только `defineTheme`.
 */
export const MONACO_THEME = 'bhs';

/**
 * Роли палитры, из которых собрана тема, — полными именами переменных: голое имя роли в строке сторож
 * цветовых классов принял бы за класс без токена (`outline-variant` — утилита `outline-*`).
 */
export const ROLE = {
  surface: '--md-sys-color-surface',
  onSurface: '--md-sys-color-on-surface',
  onSurfaceVariant: '--md-sys-color-on-surface-variant',
  surfaceContainer: '--md-sys-color-surface-container',
  outlineVariant: '--md-sys-color-outline-variant',
  primary: '--md-sys-color-primary',
  secondary: '--md-sys-color-secondary',
  tertiary: '--md-sys-color-tertiary',
  error: '--md-sys-color-error',
  secondaryContainer: '--md-sys-color-secondary-container',
  onSecondaryContainer: '--md-sys-color-on-secondary-container',
} as const;

/** Значение роли по имени её переменной (`ROLE.surface`) — строка `#rrggbb`. */
type Role = (variable: string) => string;

/** Прозрачность как суффикс `#rrggbbaa`: выделение — 16 %, неактивное — 8 %. */
const ALPHA_16 = '29';
const ALPHA_8 = '14';

/** Чистая функция «роли → тема»: всё, что видит пользователь, здесь, без DOM и Monaco. */
export function bhsTheme(role: Role, dark: boolean): editor.IStandaloneThemeData {
  // Правила токенов Monaco принимают цвет без «#».
  const fg = (variable: string) => role(variable).slice(1);
  return {
    base: dark ? 'vs-dark' : 'vs',
    inherit: true,
    // Подсветка — только системными ролями, без смыслового цвета (success, warning): токены
    // `typstLanguage.ts`, по префиксу правила ловят и родственные (`string.math`, `string.quote`).
    rules: [
      { token: '', foreground: fg(ROLE.onSurface) },
      { token: 'comment', foreground: fg(ROLE.onSurfaceVariant), fontStyle: 'italic' },
      { token: 'keyword', foreground: fg(ROLE.primary) },
      { token: 'entity.name.function', foreground: fg(ROLE.tertiary) },
      { token: 'constant', foreground: fg(ROLE.tertiary) },
      { token: 'string', foreground: fg(ROLE.secondary) },
      { token: 'markup.bold', fontStyle: 'bold' },
      { token: 'markup.italic', fontStyle: 'italic' },
    ],
    colors: {
      'editor.background': role(ROLE.surface),
      'editor.foreground': role(ROLE.onSurface),
      'editorGutter.background': role(ROLE.surface),
      'editorLineNumber.foreground': role(ROLE.onSurfaceVariant),
      'editorLineNumber.activeForeground': role(ROLE.onSurface),
      'editorCursor.foreground': role(ROLE.primary),
      'editor.selectionBackground': role(ROLE.primary) + ALPHA_16,
      'editor.inactiveSelectionBackground': role(ROLE.primary) + ALPHA_8,
      // Текущая строка — заливкой без рамки: у базы `vs` рамка своя, серым не из палитры.
      'editor.lineHighlightBackground': role(ROLE.surfaceContainer),
      'editor.lineHighlightBorder': role(ROLE.surfaceContainer),
      'editorIndentGuide.background1': role(ROLE.outlineVariant),
      'editorError.foreground': role(ROLE.error),
      // Подсказки, поиск и всплывающие окна — слой поверхностью меню ([UI-76]).
      'editorWidget.background': role(ROLE.surfaceContainer),
      'editorWidget.foreground': role(ROLE.onSurface),
      'editorWidget.border': role(ROLE.outlineVariant),
      'editorSuggestWidget.background': role(ROLE.surfaceContainer),
      'editorSuggestWidget.foreground': role(ROLE.onSurface),
      'editorSuggestWidget.border': role(ROLE.outlineVariant),
      'editorSuggestWidget.selectedBackground': role(ROLE.secondaryContainer),
      'editorSuggestWidget.selectedForeground': role(ROLE.onSecondaryContainer),
      'editorHoverWidget.background': role(ROLE.surfaceContainer),
      'editorHoverWidget.foreground': role(ROLE.onSurface),
      'editorHoverWidget.border': role(ROLE.outlineVariant),
    },
  };
}

/**
 * Роль из вычисленных стилей документа. Непонятный цвет Monaco молча рисует красным — поэтому отказ
 * громкий: тема без стилей палитры (тест без CSS, сломанная сборка) должна падать, а не краснеть.
 */
export function readRole(variable: string): string {
  const value = getComputedStyle(document.documentElement).getPropertyValue(variable).trim();
  if (!/^#[0-9a-f]{6}$/i.test(value))
    throw new Error(`Тема редактора: роль ${variable} не цвет #rrggbb («${value}»)`);
  return value;
}

const isDark = () => document.documentElement.dataset.theme === 'dark';

/**
 * Определить тему и следить за сменой темы приложения; вернуть отписку. Зовёт её загрузчик Monaco один
 * раз — до первого редактора, так что тема не мигает.
 *
 * Тему меняет атрибут `data-theme` у `<html>` — его пишут `theme-init.js` до отрисовки и `ThemeProvider`
 * в своём эффекте. Эффекты детей идут раньше эффекта родителя, и редактор, перечитывающий роли по
 * контексту темы, прочёл бы прежние; наблюдатель за атрибутом видит уже записанное. Переопределение
 * текущей темы Monaco применяет ко всем редакторам сам — без `setTheme` и пересоздания.
 */
export function installBhsTheme(api: Pick<typeof editor, 'defineTheme'>): () => void {
  const define = () => api.defineTheme(MONACO_THEME, bhsTheme(readRole, isDark()));
  define();
  const observer = new MutationObserver(define);
  observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
  return () => observer.disconnect();
}
