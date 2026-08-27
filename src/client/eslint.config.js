import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import tseslint from 'typescript-eslint'
import { defineConfig, globalIgnores } from 'eslint/config'

export default defineConfig([
  // Помимо сборки — артефакты инструментов, которые git игнорирует, а линт видел. Они лежат в
  // рабочем каталоге у того, кто эти инструменты запускал, и не существуют больше нигде: у
  // соседа, в CI. Так и вышло — сгенерированный `.ds-entry.tsx` (барель design-sync, .gitignore
  // репозитория) давал 25 ошибок из 137, и базовый уровень храповика, снятый локально, разошёлся
  // с прогоном в CI на ровном месте (issue #854). Правило простое: исходников с точки в начале
  // имени в проекте нет, значит это чужая генерация.
  globalIgnores(['dist', '**/.*.ts', '**/.*.tsx']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      js.configs.recommended,
      tseslint.configs.recommended,
      reactHooks.configs.flat.recommended,
      reactRefresh.configs.vite,
    ],
    languageOptions: {
      globals: globals.browser,
    },
    rules: {
      // Имя с подчёркиванием впереди — «я знаю, что не использую». Так в проекте уже записана
      // единственная идиома, которой это нужно: выбросить ключ из объекта разбором —
      // `const { [SUMMARY]: _omit, ...rest } = values`. Правило про неиспользуемое при этом
      // остаётся в силе для всего остального: договорённость названа явно, а не выключена
      // (issue #858).
      '@typescript-eslint/no-unused-vars': ['error', {
        argsIgnorePattern: '^_',
        varsIgnorePattern: '^_',
        caughtErrorsIgnorePattern: '^_',
        ignoreRestSiblings: true,
      }],
      // crypto.randomUUID есть только в ЗАЩИЩЁННОМ контексте — по HTTPS или на localhost. Установка,
      // открытая по http://<ip>:8080 (руководство по развёртыванию такие допускает внутри доверенной
      // сети), метода не получает, и вызов роняет экран целиком: в инициализаторе useState он падает
      // на первом же рендере. Так и вышло с панелью параметров шаблона (issue #848) — а заметно это
      // только тем, у кого нет HTTPS, то есть проверка «у меня работает» ничего не значит.
      //
      // Правило — подсказка в редакторе, а НЕ ворота: линт не запускается ни в одном workflow и
      // локально уже отвечает сотней ошибок, накопленных раньше. Настоящий сторож — тест
      // src/shared/utils/secureContextApis.test.ts: он виден в `npm test` и падает один.
      'no-restricted-syntax': ['error',
        {
          selector: "MemberExpression[property.name='randomUUID']",
          message: 'crypto.randomUUID недоступен по HTTP — берите newLocalId() из @/shared/utils/localId (issue #848).',
        },
        {
          // crypto['randomUUID']() — то же самое, мимо селектора выше.
          selector: "MemberExpression[computed=true][property.value='randomUUID']",
          message: 'crypto.randomUUID недоступен по HTTP — берите newLocalId() из @/shared/utils/localId (issue #848).',
        },
        {
          // const { randomUUID } = crypto — вызов уедет уже без слова «crypto».
          selector: "ObjectPattern > Property[key.name='randomUUID']",
          message: 'crypto.randomUUID недоступен по HTTP — берите newLocalId() из @/shared/utils/localId (issue #848).',
        },
      ],
    },
  },
  {
    // Единственное место, где обращение к randomUUID уместно: сама утилита и её тест — они этот
    // вызов и заворачивают.
    files: ['src/shared/utils/localId.ts', 'src/shared/utils/localId.test.ts'],
    rules: { 'no-restricted-syntax': 'off' },
  },
])
