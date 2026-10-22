# Tasks: храповик интерфейса смотрит `shared/`, кроме `shared/ui`

Один PR с «Closes #1469». Без новой функциональности — PATCH, метка `version:minor` не нужна.

## 1. Храповик

- [ ] 1.1 Область по пути: правило без `dirs` — `features/**` и `shared/**` без `shared/ui/**`;
  `white-on-fill`, `portal-container`, `native-date` — как были; обход — `features`, `shared` (+ корень
  для `everywhere`) [UI-47]. Проверка — `ui-ratchet.test.mjs`: `shared/filter` в обходе, `shared/ui` —
  нет для общих правил и да для `white-on-fill`
- [ ] 1.2 `fixed-width` без медиазапросов: `(?<![\w-])` перед именем свойства [UI-47]. Проверка — тест:
  `'(min-width: 768px)'` не считается, `style={{ width: 120 }}` считается; `npm run ui:ratchet:update` на
  дереве до переводов меняет только раздел `shared/`
- [ ] 1.3 Сводка храповика называет новую область (`ui-ratchet.mjs`, первая строка вывода) [UI-47].
  Проверка — вывод `npm run ui:ratchet`

## 2. Отбор реестров

- [ ] 2.1 `shared/filter/ConditionEditor.tsx`: колонка и оператор → `Select size="sm"`, `emptyItem`
  «— колонка —», недоступная колонка — `SelectItem disabled` с причиной, выбранная отсутствующая — на
  месте; ширины `style` → классы шкалы [UI-48]. Проверка — тест: выбор колонки зовёт `withColumn`,
  отсутствующая колонка показана выбранной
- [ ] 2.2 `shared/filter/RowFilterValue.tsx`: «да / нет» и выбор из списка → `Select size="sm"`; фиксация
  по закрытию списка вместо `onBlur`; кнопки-иконки → `IconButton` [UI-48]. Проверка — тест
  `RowFilterValue`: выбор «нет» доходит до `onChange`, закрытие списка зовёт `onBlur`
- [ ] 2.3 Поповер чипа с `Select` внутри: выбор не закрывает поповер, Esc закрывает список, а не
  поповер [UI-48]. Проверка — тест `FilterChips`; живой прогон `table-chips` и e2e отбора наборов
  данных (`RowFilterDialog`) зелёные; ручная проверка на стенде — описание PR
- [ ] 2.4 `shared/filter/FilterChips.tsx` и `shared/siteFilter/SiteFilterNotes.tsx`: кнопки-иконки →
  `IconButton`, чип → `Chip` [UI-47]. Проверка — `npm run ui:ratchet`: в `shared/` ноль мест

## 3. Документы

- [ ] 3.1 `docs/UI.md`: таблица контролов («Нативный `<select>` не ставится нигде, кроме `shared/ui`»),
  «Кто сторожит» — область храповика `features/` и `shared/` без `shared/ui`; перечень храповика в
  `CLAUDE.md` и `AGENTS.md` — одной правкой, тем же коммитом [UI-47]. Проверка — `AgentRulesCopyTests`
  зелёный

## 4. Верификация

- [ ] 4.1 `cd src/client && npx tsc -b` — без ошибок
- [ ] 4.2 `cd src/client && npm test` — зелёный (vitest берёт и `scripts/ui-ratchet.test.mjs`)
- [ ] 4.3 `cd src/client && npm run ui:ratchet` — без роста; разделы `shared/` пусты
- [ ] 4.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~FileSizeRatchet|FullyQualifiedName~AgentRulesCopy"` — зелёный
