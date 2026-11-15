# Tasks

Меток ТЗ у задач нет: change не трогает требований (`skip_specs: true`, design D3). Разделы 1–3 —
сессия реализации, PR в репозиторий. Раздел 4 — основная сессия после слияния (design D7); сессия
реализации его не выполняет и не отмечает.

## 1. CI на 2.0.0

- [ ] 1.1 `.github/workflows/ci.yml`, работа `openspec`: `OPENSPEC_VERSION: 2.0.0`; имя работы
  («OpenSpec (строгая проверка)») и шаги не меняются; комментарий у версии — без названия прежней
  версии (D1). Проверка: локально `npx -y @fission-ai/openspec@2.0.0 validate --all --strict
  --no-interactive` зелёный (все элементы), и в PR работа «OpenSpec (строгая проверка)» зелёная с
  `openspec --version` = 2.0.0 в журнале шага установки или проверки.
- [ ] 1.2 Пробный архив каждого активного change под 2.0.0 — по одному, во временной копии (не в
  ветке): `openspec archive <change> --yes`, затем `validate --all --strict`. Проверка: итог по каждому
  записан в PR и совпадает с тем же прогоном на 1.14.3; расхождение — разобрать до слияния (правило
  валидатора 1.15–2.0 или поведение архива), а не списывать на версию.

## 2. Навыки до рабочего дерева

- [ ] 2.1 `.worktreeinclude`: строка `.agents/skills/` с комментарием — ссылки `.claude/skills/openspec-*`
  относительные и ведут в `.agents/skills/` своего дерева; без копии навыки пропадают молча (D5).
  Проверка: `git check-ignore -v .agents/skills/openspec-propose/SKILL.md` называет правило
  `.gitignore` (копируется только игнорируемое), файл читается глазами — блок «Чего здесь нет» не
  противоречит новой строке.

## 3. Правила и документация

- [ ] 3.1 CLAUDE.md и AGENTS.md **одним коммитом**: команда установки — `@fission-ai/openspec@2.0.0`;
  «вход для `/opsx:propose`» → `/openspec-propose`; «реализуют `/opsx:apply <change>`» →
  `/openspec-apply-change <change>` (D2). Проверка: `git grep -n "opsx" -- CLAUDE.md AGENTS.md` пусто;
  `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter AgentRulesCopyTests` зелёный.
- [ ] 3.2 `docs/tz/README.md`, `docs/tz/90-этапы.md`, `openspec/specs/id/spec.md` (раздел «Ожидает
  будущих этапов», вне требований — D3): `/opsx:propose` → `/openspec-propose`. Проверка:
  `git grep -n "opsx" -- ':!openspec/changes'` пусто; строгая проверка 2.0.0 зелёная (спека `id` не
  потеряла ни требования, ни `## Purpose`).
- [ ] 3.3 `docs/DEV_NOTES.md`, рядом с абзацем о строгой проверке OpenSpec: абзац «OpenSpec 2.0» —
  таблица «команда 1.x → навык 2.0» (12 строк по руководству openspec.dev/docs/upgrade-to-2; профиль
  `core` ставит шесть), где лежат навыки и что такое ссылки `.claude/skills/`, зачем строка в
  `.worktreeinclude` (D5), почему записи в `openspec/changes/**` остались с `/opsx:*` (D4), откат.
  Проверка: ссылка на абзац из CLAUDE.md не нужна (CLAUDE.md называет навыки сам), абзац находится
  поиском `openspec-apply-change` в DEV_NOTES.

## 4. Машина (основная сессия, после слияния, в паузе)

- [ ] 4.1 Пауза: нет сессий посреди change (`list_sessions`), идущие доведены до остановки. Проверка:
  список связанных сессий без работающих над change.
- [ ] 4.2 Копия `E:\Development\Office\BHS.CRG\.agents\skills` в сторону (scratchpad); `npm install -g
  @fission-ai/openspec@2.0.0`. Проверка: `openspec --version` = 2.0.0.
- [ ] 4.3 В основной копии `openspec update --dry-run`, затем `openspec update`. Проверка: нет
  `.claude/commands/opsx/`; `cmd /c dir /AL .claude\skills` — шесть `<SYMLINKD>` на
  `..\..\.agents\skills\openspec-*`; второй `openspec update` ничего не меняет; `git status` основной
  копии чистый.
- [ ] 4.4 Плагин `~/.claude/local-plugins/openspec-band`: `hooks/register.tsx` — вызов навыка
  принимается и с префиксом `opsx:`, и с `openspec-` (D8), комментарий у `FLAG`; `hooks/band.test.tsx` —
  случай с `skill: 'openspec-apply-change'`. Проверка: тесты плагина зелёные, оба случая.
- [ ] 4.5 Новая сессия в своём рабочем дереве: в списке навыков есть `openspec-propose` и
  `openspec-apply-change`, вызов `/openspec-explore` (без записи) отрабатывает, полоса показывает change
  после вызова навыка с именем. Если навыков нет — посмотреть, что приложение скопировало в
  `.claude/skills/` и `.agents/skills/` дерева, и строкой `.claude/skills/` в `.worktreeinclude`
  отдельным PR (D5). Проверка: снимок списка навыков в отчёте.
- [ ] 4.6 Память агента: в файлах памяти `/opsx:*` → навыки 2.0 (сейчас 14 файлов, `grep -rl opsx` по
  каталогу памяти), запись «OpenSpec 2.0» в указателе. Проверка: `grep -rn "opsx:"` по каталогу
  памяти — только упоминания, прямо названные прежними именами.

## 5. Верификация

- [ ] 5.1 `npx -y @fission-ai/openspec@2.0.0 validate --all --strict --no-interactive` — все зелёные.
- [ ] 5.2 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter
  "AgentRulesCopyTests|FileSizeRatchet"` — зелёный (сервер не тронут — остальные backend-тесты не
  нужны).
- [ ] 5.3 `cd src/client && npx tsc -b` и `cd src/client && npm run ui:ratchet` — зелёные (клиент не
  тронут; проверка, что не задет).
- [ ] 5.4 PR: CI зелёный — три обязательные работы, «OpenSpec (строгая проверка)» на 2.0.0, живые
  прогоны.

## Workflow follow-up

- Архив change отдельным PR после слияния и раздела 4 (`/openspec-archive-change infra-openspec-2`).
- Сделать ли «OpenSpec (строгая проверка)» обязательной — решение владельца (правило «Master ruleset»).
