# Як опублікувати MatchLens на GitHub

## 1. Підготуй папку

Розпакуй `MatchLens_0.4.9_GitHub.zip`. Усередині є папка `MatchLens_GitHub` з README, скриншотами та кодом у `src/MatchLens`.

Саме її вміст має бути в корені репозиторію. Готовий архів `MatchLens_0.4.9_Windows.zip` потрібен окремо для Releases.

## 2. Створи репозиторій

На GitHub натисни **+ → New repository**. Назва — **MatchLens**. Опис:

> CS2 player insights, public stats and Telegram summaries in a clean Windows app.

Вибери **Public**, якщо хочеш, щоб усі бачили код. Не додавай початкові README, .gitignore або ліцензію: необхідні файли вже підготовлені, а ліцензію можна вибрати окремо. Натисни **Create repository**.

Посилання в README підготовлені для `ThunderBoldX/MatchLens`. Якщо твій логін або назва репозиторію інші, заміни `ThunderBoldX/MatchLens` у README.md, README.uk.md та командах нижче.

## 3. Завантаж код

Відкрий термінал у папці `MatchLens_GitHub`: наприклад, відкрий її у VS Code та натисни **Terminal → New Terminal**. Виконай:

```powershell
git init -b main
git add .
git commit -m "Initial release: MatchLens 0.4.9"
git remote add origin https://github.com/ThunderBoldX/MatchLens.git
git push -u origin main
```

Якщо з'явиться вхід у GitHub — увійди через браузер. Якщо Git попросить ім'я та пошту автора, задай їх для цієї папки й повтори commit:

```powershell
git config user.name "Твоє ім'я"
git config user.email "Твоя пошта GitHub"
```

Онови сторінку репозиторію. README та скриншоти відобразяться автоматично; посилання **Українська** відкриває переклад.

## 4. Додай готову програму

У репозиторії відкрий **Releases → Create a new release** або **Draft a new release**.

- **Tag:** `v0.4.9` → Create new tag.
- **Target:** `main`.
- **Title:** `MatchLens 0.4.9`.
- **Description:** скопіюй текст із `docs/RELEASE_0.4.9.md`.
- **Assets:** перетягни `MatchLens_0.4.9_Windows.zip` і його файл `.sha256`, якщо використовуєш його.

Натисни **Publish release**. Користувачі завантажуватимуть програму з Assets. Автоматичний **Source code (zip)** містить код і не замінює архів готової програми.

## 5. Оформи сторінку

У блоці **About → ⚙** додай опис і теми: `cs2`, `counter-strike-2`, `matchlens`, `wpf`, `telegram`, `player-stats`. Познач **Releases**, щоб готовий файл було легко знайти.

Для наступних оновлень:

```powershell
git add .
git commit -m "Update MatchLens"
git push
```

Після цього створи новий Release з новим тегом і новим ZIP програми.

Довідка: [завантаження локального коду](https://docs.github.com/en/migrations/importing-source-code/using-the-command-line-to-import-source-code/adding-locally-hosted-code-to-github) · [створення релізів](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository).
