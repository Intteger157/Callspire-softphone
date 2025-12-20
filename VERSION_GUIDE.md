# Руководство по изменению версии приложения

## Где изменить версию

Версия приложения задается в файле **`Callspire.csproj`** в секции `<PropertyGroup>`:

```xml
<!-- Application Version -->
<!-- Измените эти значения перед каждым релизом -->
<Version>1.0.0</Version>
<AssemblyVersion>1.0.0.0</AssemblyVersion>
<FileVersion>1.0.0.0</FileVersion>
<InformationalVersion>1.0.0</InformationalVersion>
```

## Формат версии

- **`<Version>`** - основная версия в формате `x.y.z` (например, `1.0.0`, `1.1.0`, `2.0.0`)
- **`<AssemblyVersion>`** - версия сборки в формате `x.y.z.w` (например, `1.0.0.0`)
- **`<FileVersion>`** - версия файла в формате `x.y.z.w` (обычно совпадает с AssemblyVersion)
- **`<InformationalVersion>`** - информационная версия, отображаемая пользователю (формат `x.y.z`)

## Как изменить версию перед релизом

### Пример: обновление с 1.0.0 до 1.0.1

1. Откройте файл `Callspire.csproj`
2. Найдите секцию с версиями (строки 23-27)
3. Измените все значения:

```xml
<Version>1.0.1</Version>
<AssemblyVersion>1.0.1.0</AssemblyVersion>
<FileVersion>1.0.1.0</FileVersion>
<InformationalVersion>1.0.1</InformationalVersion>
```

### Пример: обновление до 1.1.0

```xml
<Version>1.1.0</Version>
<AssemblyVersion>1.1.0.0</AssemblyVersion>
<FileVersion>1.1.0.0</FileVersion>
<InformationalVersion>1.1.0</InformationalVersion>
```

### Пример: обновление до 2.0.0

```xml
<Version>2.0.0</Version>
<AssemblyVersion>2.0.0.0</AssemblyVersion>
<FileVersion>2.0.0.0</FileVersion>
<InformationalVersion>2.0.0</InformationalVersion>
```

## Важно!

1. **Версия должна совпадать с тегом релиза на GitHub** (например, если версия `1.0.1`, то тег должен быть `v1.0.1` или `1.0.1`)
2. **Измените версию ПЕРЕД сборкой** релиза
3. **После изменения версии пересоберите проект** (`Build` → `Rebuild Solution`)
4. **Проверьте версию** в Settings → About после сборки

## Где отображается версия

- **Settings → About** - отображается как "Version: x.y.z"
- **Проверка обновлений** - сравнивается с версией релиза на GitHub
- **Свойства файла .exe** - в деталях файла (FileVersion)

## Семантическое версионирование

Рекомендуется использовать [Semantic Versioning](https://semver.org/):

- **MAJOR** (x.0.0) - несовместимые изменения API
- **MINOR** (x.y.0) - новая функциональность с обратной совместимостью
- **PATCH** (x.y.z) - исправления ошибок с обратной совместимостью

Примеры:
- `1.0.0` → `1.0.1` - исправление бага
- `1.0.1` → `1.1.0` - добавление новой функции
- `1.1.0` → `2.0.0` - крупное обновление с несовместимыми изменениями

