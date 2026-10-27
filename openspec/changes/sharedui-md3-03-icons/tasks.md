# Tasks: значки Material Symbols и индикатор хода

Два PR после слияния `sharedui-md3-02-class-codemod`, оба с меткой `version:minor`.

## 0. До кода

- [ ] 0.1 Лист в Claude Design — три варианта на одной доске (зонтик, D10): стиль Outlined / Rounded (и
  вес, если владелец захочет не 400), заливка у выбранного, шкала размеров в плотной строке таблицы, в
  кнопке, чипе, рейле — на реальных местах (реестр счетов, строка счёта, рейл, окно). Проверка — ссылка
  на лист в зонтике и в этом change
- [ ] 0.2 Выбор владельца по В4 и по шкале размеров записан в зонтике (`tasks.md`, 0.1, с датой) и
  перенесён сюда `/opsx:update`: стиль и пакет, ступени шкалы в `design.md` и в требовании [UI-60] о
  размере. Проверка — `openspec validate sharedui-md3-03-icons --strict`; в `design.md` нет слова
  «рекомендация» у шкалы
- [ ] 0.3 Замер основного чанка master: `npm run build`, gzip основного чанка и число значков lucide в нём;
  бюджет `design.md` подтверждён или переписан до кода [UI-60]. Проверка — оба числа в `design.md`
- [ ] 0.4 Таблица lucide → Symbols на 183 значка (`md3-icon-map.json`) от Дизайнера, синонимы сведены,
  `fill` отмечен у выбранного; лист соответствия показан владельцу [UI-60]. Проверка — в таблице каждое имя
  из `rg -o "from 'lucide-react'"` по импортам, ни одно не пропущено (проверка скриптом из 2.2)

## 1. Основа (PR 1)

- [ ] 1.1 `@material-symbols/svg-400` (или пакет выбранного веса) — devDependency с точной версией;
  `scripts/icons-generate.mjs`, `npm run icons:generate`, `shared/ui/icons/manifest.json`,
  `icons.generated.ts` [UI-60]. Проверка — тест дрейфа: правка манифеста без перегенерации — красный с
  командой; значок вне манифеста — ошибка `tsc -b`
- [ ] 1.2 `shared/ui/Icon.tsx` и тип `IconDef`: `viewBox`, `currentColor`, `aria-hidden` по умолчанию,
  `filled` только у значка с вариантом `-fill`, `aria-label` → `role="img"` [UI-60]. Проверка — vitest:
  `textContent` кнопки со значком без имени значка; `filled` рисует путь `-fill`; ошибка типа на `filled` у
  значка без заливки (`@ts-expect-error` в тесте)
- [ ] 1.3 `shared/ui/CircularProgress.tsx`: indeterminate, `label` → `role="progressbar"` и имя, без
  `label` — `aria-hidden`, `prefers-reduced-motion` [UI-61]. Проверка — vitest на обе ветви доступности
- [ ] 1.4 Правила `lucide-import`, `spinner-icon` (уровни по файлам), `icon-size` (0); `icon-button` знает
  `Icon` [UI-60] [UI-61]. Проверка — `ui-ratchet.test.mjs`: импорт `lucide-react` и `@material-symbols/…` в
  экране, `animate-spin` у значка, `size={13}` у `Icon`, `<button><Icon …/></button>` — считаются;
  `animate-spin` в самом `CircularProgress` (исключение путём, `skip`) — нет
- [ ] 1.5 `docs/UI.md` — раздел «Значки» (вход, шкала, заливка у выбранного, индикатор хода, как добавить
  значок) и строки в «Кто сторожит»; `docs/DEV_NOTES.md` — лицензия Apache 2.0; перечень правил в
  `CLAUDE.md` и `AGENTS.md` одним коммитом [UI-60] [UI-61]. Проверка — `dotnet test … --filter
  AgentRulesCopy` зелёный

## 2. Перевод экранов (PR 2)

- [ ] 2.1 `scripts/md3-icons-migrate.mjs` по таблице 0.4, вызов из `npm run md3:migrate`: импорты, JSX,
  ссылки-значения, `LucideIcon` → `IconDef`, `strokeWidth`, `Loader2 animate-spin` → `CircularProgress`,
  размеры — на ступень шкалы; значок вне таблицы — отказ с перечнем; отчёт значков в низких контейнерах
  [UI-60] [UI-61]. Проверка — vitest кодмода на фикстурах (импорт с `as`, значение в карте, тип, спиннер в
  кнопке); второй прогон — «замен 0»
- [ ] 2.2 Прогон по `src/`; 7 тестов с lucide переведены; `lucide-react` удалён из `package.json`
  [UI-60]. Проверка — `rg "lucide" src/client/src src/client/package.json` пусто; `npx tsc -b` зелёный
- [ ] 2.3 Уровни `lucide-import` и `spinner-icon` — 0 [UI-60] [UI-61]. Проверка — `npm run ui:ratchet`
  зелёный, в `ui-baseline.json` правила пусты
- [ ] 2.4 Геометрия живых прогонов (25 файлов с `boundingBox`) поправлена там, где выросли значки, — с
  объяснением в описании PR [UI-60]. Проверка — весь `test:e2e:ci` зелёный; ни одна проверка не ослаблена
  без причины в описании
- [ ] 2.5 Бюджет: `npm run build` на master и ветке, gzip основного чанка [UI-60]. Проверка — разница не
  больше бюджета `design.md`, оба числа в описании PR
- [ ] 2.6 Снимки 5–7 ключевых экранов до и после в обеих темах на 1366×768 и 375 px (зонтик, 5.2).
  Проверка — снимки в описании PR
- [ ] 2.7 После слияния — `/design-sync` из основной копии: превью и `config.json` без lucide. Проверка —
  в превью дизайн-системы нет `lucide-react`

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~AgentRulesCopy"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок; `npm test` — зелёный
- [ ] 3.3 `cd src/client && npm run ui:ratchet` и `npm run lint:ratchet` — без роста; `lucide-import`,
  `spinner-icon`, `icon-size` — 0
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
- [ ] 3.5 `cd src/client && npm run -s test:e2e:ci` — весь список живых прогонов зелёный в каждом из двух
  PR: значки стоят в общих компонентах и оболочке (зонтик, 5.1)
