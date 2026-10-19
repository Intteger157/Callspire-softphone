# Руководство по версии и релизу приложения

Релизы Callspire **не собираются локально**. Сборка и упаковка (установщики Windows, macOS `.zip`/`.dmg`) выполняются автоматически в GitHub Actions после push.

## Где задаётся версия

Единственный источник версии — **`Directory.Build.props`** в корне репозитория:

```xml
<PropertyGroup>
  <AppVersion>1.1.50</AppVersion>
  <Version>$(AppVersion)</Version>
  <AssemblyVersion>$(AppVersion).0</AssemblyVersion>
  <FileVersion>$(AppVersion).0</FileVersion>
  <InformationalVersion>$(AppVersion)</InformationalVersion>
</PropertyGroup>
```

Меняйте **только `AppVersion`** (формат `x.y.z`) — остальные значения вычисляются из него и применяются ко всем проектам решения (Core, AppHost, Desktop, Service). В отдельных `.csproj` версию не править.

## Процесс релиза

1. Измените `AppVersion` в `Directory.Build.props` (например, `1.1.50` → `1.1.51`).
2. Закоммитьте и сделайте push в GitHub.
3. Для публикации релиза создайте и отправьте тег, **совпадающий с версией** с префиксом `v`:

   ```bash
   git tag v1.1.51
   git push origin v1.1.51
   ```

## Что делает GitHub

| Событие | Workflow | Результат |
|---|---|---|
| push в любую ветку / PR в main | `.github/workflows/build.yml` | Артефакты Windows x64/arm64 и macOS arm64/x64 (unsigned), хранятся 30 дней |
| push тега `v*` (или ручной запуск) | `.github/workflows/release.yml` | В GitHub Release загружаются: `Callspire-<версия>-win-x64-setup.exe`, `Callspire-<версия>-win-arm64-setup.exe`, `Callspire-<версия>-macos-{arm64,x64}-unsigned.zip` / `.dmg` |

Версия установщика Windows берётся из имени тега (без `v`), поэтому тег должен совпадать с `AppVersion`. Теги с `-` (например, `v1.1.51-beta1`) публикуются как pre-release.

## Важно

- Не собирайте релизные артефакты локально через `build.ps1` / `build.sh` — эти скрипты только для локальной проверки компиляции и отладки.
- Меняйте версию **до** создания тега.
- Не используйте старый `Callspire.csproj` — версионирование теперь централизовано в `Directory.Build.props`.
