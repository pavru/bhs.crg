# Tasks

Меток ТЗ у задач нет: change не трогает требований (`skip_specs: true`, design D3). Разделы 1–3 —
сессия реализации, PR в репозиторий. Раздел 4 — основная сессия после слияния (design D7); сессия
реализации его не выполняет и не отмечает.

## 1. CI на 2.0.0

- [x] 1.1 `.github/workflows/ci.yml`, работа `openspec`: `OPENSPEC_VERSION: 2.0.0`; шаг установки
  заканчивается `openspec --version` (ни `npm install`, ни `validate` версию не печатают); имя работы
  («OpenSpec (строгая проверка)») не меняется — договор с правилом слияния по имени работы, а не шага;
  комментарий у версии — без названия прежней версии (D1). Проверка: локально `npx -y
  @fission-ai/openspec@2.0.0 validate --all --strict --no-interactive` зелёный (все элементы); в PR
  работа зелёная, в журнале шага установки — `2.0.0`.
- [x] 1.2 Пробный архив всех активных change разом — по одной временной копии ветки (не в самой ветке)
  на 1.14.3 и на 2.0.0: в каждой `openspec archive <change> --yes` по всем активным change в одном
  порядке (по имени) с записью кода выхода и сообщения каждого, затем `validate --all --strict`.
  Проверка: журналы двух версий совпадают по кодам выхода, итоговые `openspec/specs/**` двух копий —
  `diff -r` пуст; расхождение разобрать до слияния (правило валидатора 1.15–2.0 или поведение архива),
  итог — в описании PR. Отказ архива, одинаковый на обеих версиях (незакрытые задачи, порядок
  зависимых change), — не находка этой задачи.

## 2. Навыки до рабочего дерева

- [x] 2.1 `.worktreeinclude`: строки `.agents/skills/` и `.claude/skills/` с комментарием — ссылки
  `.claude/skills/openspec-*` относительные и ведут в `.agents/skills/` своего дерева; копирует ли
  приложение `.claude/skills/` само, неизвестно; лишняя строка безвредна, недостающая — навыки
  пропадают молча (D5). Проверка: `git check-ignore -v` для `.agents/skills/openspec-propose/SKILL.md` и
  `.claude/skills/openspec-propose/SKILL.md` называет правило `.gitignore` (копируется только
  игнорируемое); оговорка «только файл, не каталог» у `.claude/launch.json` переписана так, что
  подкаталог `skills/` ей не противоречит.

## 3. Правила и документация

- [x] 3.1 CLAUDE.md и AGENTS.md **одним коммитом**: команда установки — `@fission-ai/openspec@2.0.0`;
  «вход для `/opsx:propose`» → `/openspec-propose`; «реализуют `/opsx:apply <change>`» →
  `/openspec-apply-change <change>` (D2). Проверка: `git grep -n "opsx" -- CLAUDE.md AGENTS.md` пусто;
  `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter AgentRulesCopyTests` зелёный.
- [x] 3.2 `docs/tz/README.md`, `docs/tz/90-этапы.md`, `openspec/specs/id/spec.md` (раздел «Ожидает
  будущих этапов», вне требований — D3): `/opsx:propose` → `/openspec-propose`. Проверка:
  `git grep -n "opsx" -- ':!openspec/changes' ':!docs/DEV_NOTES.md'` пусто (в DEV_NOTES старые имена
  остаются нарочно — таблица соответствий задачи 3.3); строгая проверка 2.0.0 зелёная (спека `id` не
  потеряла ни требования, ни `## Purpose`).
- [x] 3.3 `docs/DEV_NOTES.md`, рядом с абзацем о строгой проверке OpenSpec: абзац «OpenSpec 2.0» —
  таблица «команда 1.x → навык 2.0» (12 строк по руководству openspec.dev/docs/upgrade-to-2; профиль
  `core` ставит шесть), где лежат навыки и что такое ссылки `.claude/skills/`, зачем строка в
  `.worktreeinclude` (D5), почему записи в `openspec/changes/**` остались с `/opsx:*` (D4), откат.
  Проверка: ссылка на абзац из CLAUDE.md не нужна (CLAUDE.md называет навыки сам), абзац находится
  поиском `openspec-apply-change` в DEV_NOTES.

## 4. Машина (основная сессия, после слияния, в паузе)

- [x] 4.1 Пауза: нет сессий посреди change (`list_sessions`), идущие доведены до остановки. Проверка:
  список связанных сессий без работающих над change.
  Итог (16.11.2026): `list_sessions` — ни одной работающей сессии.
- [x] 4.2 Копия `E:\Development\Office\BHS.CRG\.agents\skills` в сторону (scratchpad); `npm install -g
  @fission-ai/openspec@2.0.0`. Проверка: `openspec --version` = 2.0.0.
  Итог: копии `.agents/skills` и `.claude/commands/opsx` — в scratchpad основной сессии; версия 2.0.0.
- [x] 4.3 В основной копии `openspec update --dry-run --json`: в списке только пути под `.agents/` и
  `.claude/`, `failures` пуст — иначе стоп до разбора (design, «Risks»). Затем `openspec update`.
  Проверка: нет `.claude/commands/opsx/`; `cmd /c dir /AL .claude\skills` — шесть `<SYMLINKD>` на
  `..\..\.agents\skills\openspec-*`; второй `openspec update` ничего не меняет; `git status` основной
  копии чистый (не чистый — `git restore` тронутого и стоп).
  Итог: `--dry-run` — 19 путей, только `.agents/` и `.claude/`, `failures` пуст; `update` удалил шесть
  команд, `.claude/commands/` нет; шесть `<SYMLINKD>` на `..\..\.agents\skills\openspec-*`; повторный
  `--dry-run` — «No changes: skills are up to date»; `git status` чистый; строгая проверка — 45/45.
- [x] 4.4 Плагин `~/.claude/local-plugins/openspec-band`: `hooks/register.tsx` — вызов навыка
  принимается и с префиксом `opsx:`, и с `openspec-` (D8), комментарий у `FLAG`; `hooks/band.test.tsx` —
  случай с `skill: 'openspec-apply-change'`. Проверка: тесты плагина зелёные, оба случая.
  Итог: тест — `opsx:apply`, `openspec-apply-change` и чужой навык (не выбирается); `claude plugin
  test` — 5/5, `validate` прошёл.
- [x] 4.5 Новая сессия в своём рабочем дереве: в списке навыков есть `openspec-propose` и
  `openspec-apply-change`, вызов `/openspec-explore` (без записи) отрабатывает, полоса показывает change
  после вызова навыка с именем. Если навыков нет — посмотреть, что оказалось в `.claude/skills/` и
  `.agents/skills/` дерева (ссылки, копии, пусто), и разобрать до возобновления потоков. Проверка:
  снимок списка навыков и содержимого обоих каталогов дерева в отчёте.
  Итог: сессия видит все шесть `openspec-*`, `opsx:*` нет; `/openspec-explore` отработал на CLI 2.0.0.
  ⚠️ Но не так, как ждал D5: `.claude/skills/` в дереве не появился вовсе (ссылки на каталоги
  `.worktreeinclude` не копирует), `.agents/skills/` приехал настоящими копиями, а навык взят из
  ОСНОВНОЙ копии (`E:\Development\Office\BHS.CRG\.claude\skills\…`). Дерево не самодостаточно —
  приложение читает навыки проекта из основной копии. Строка `.claude/skills/` из `.worktreeinclude`
  убрана, комментарий и DEV_NOTES поправлены — тем же PR, что архив.
- [x] 4.6 Память агента: в файлах памяти `/opsx:*` → навыки 2.0 (сейчас 14 файлов, `grep -rl opsx` по
  каталогу памяти), запись «OpenSpec 2.0» в указателе. Проверка: `grep -rn "opsx:"` по каталогу
  памяти — только упоминания, прямо названные прежними именами.
  Итог: инструкции памяти переведены на новые имена, запись `reference_openspec_2.md` в указателе.

## 5. Верификация

- [x] 5.1 `npx -y @fission-ai/openspec@2.0.0 validate --all --strict --no-interactive` — все зелёные.
- [x] 5.2 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter
  "AgentRulesCopyTests|FileSizeRatchet"` — зелёный. Смысл несёт `AgentRulesCopyTests`;
  `FileSizeRatchet` — по правилу верификации `openspec/config.yaml` (тронутые файлы под него не
  попадают). Остальные backend-тесты не нужны — сервер не тронут.
- [x] 5.3 `cd src/client && npx tsc -b` и `cd src/client && npm run ui:ratchet` — зелёные. Клиент не
  тронут; шаг — по тому же правилу проекта, полный CI PR прогоняет их и сам.
- [x] 5.4 PR: CI зелёный — три обязательные работы, «OpenSpec (строгая проверка)» на 2.0.0, живые
  прогоны.
  Итог: PR #1990 — всё зелёное, журнал шага установки — `2.0.0`, строгая проверка 45/45.

## Workflow follow-up

- Архив change отдельным PR после слияния и раздела 4 (`/openspec-archive-change infra-openspec-2`).
- Сделать ли «OpenSpec (строгая проверка)» обязательной — решение владельца (правило «Master ruleset»).
