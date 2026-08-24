import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import tseslint from 'typescript-eslint'
import { defineConfig, globalIgnores } from 'eslint/config'

export default defineConfig([
  globalIgnores(['dist']),
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
      // crypto.randomUUID есть только в ЗАЩИЩЁННОМ контексте — по HTTPS или на localhost. Установка,
      // открытая по http://<ip>:8080 (руководство по развёртыванию такие допускает внутри доверенной
      // сети), метода не получает, и вызов роняет экран целиком: в инициализаторе useState он падает
      // на первом же рендере. Так и вышло с панелью параметров шаблона (issue #848) — а заметно это
      // только тем, у кого нет HTTPS, то есть проверка «у меня работает» ничего не значит.
      'no-restricted-syntax': ['error', {
        selector: "MemberExpression[property.name='randomUUID']",
        message: 'crypto.randomUUID недоступен по HTTP — берите newLocalId() из @/shared/utils/localId (issue #848).',
      }],
    },
  },
  {
    // Единственное место, где обращение к randomUUID уместно: сама утилита и её тест — они этот
    // вызов и заворачивают.
    files: ['src/shared/utils/localId.ts', 'src/shared/utils/localId.test.ts'],
    rules: { 'no-restricted-syntax': 'off' },
  },
])
