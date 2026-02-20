# Интеграция CallSpire в AmoCRM через JS-виджет

**Официальный способ интеграции** через JS-виджет AmoCRM.

Документация: https://www.amocrm.ru/developers/content/integrations/script_js

## ✅ Готовый виджет

В папке `amocrm-widget/` находится готовый виджет:
- `script.js` - основной файл виджета
- `style.css` - стили виджета
- `README.md` - инструкция по установке

## Способ 1: Через настройки AmoCRM (если доступно)

1. Зайдите в **Настройки** → **Интеграции** → **Виджеты**
2. Создайте новый виджет
3. Укажите:
   - **Название:** CallSpire
   - **URL виджета:** `https://your-server.com/callspire-widget.html` (или локальный файл)
   - **Размещение:** В карточке контакта/сделки

## Способ 2: Создание виджета для AmoCRM

Создайте HTML-файл виджета, который будет добавлять кнопку в меню контакта.

### Пример виджета (callspire-widget.html)

```html
<!DOCTYPE html>
<html>
<head>
    <meta charset="UTF-8">
    <title>CallSpire Widget</title>
    <script src="https://cdn.amocrm.ru/amo_widget/v1/amo_widget.js"></script>
    <style>
        .callspire-button {
            display: inline-flex;
            align-items: center;
            padding: 8px 16px;
            background-color: #007bff;
            color: white;
            border: none;
            border-radius: 4px;
            cursor: pointer;
            font-size: 14px;
            text-decoration: none;
        }
        .callspire-button:hover {
            background-color: #0056b3;
        }
        .callspire-button svg {
            margin-right: 8px;
        }
    </style>
</head>
<body>
    <script>
        // Инициализация виджета AmoCRM
        AmoWidget.init({
            // Получаем данные контакта/сделки
            onLoad: function() {
                // Виджет загружен
            }
        });

        // Функция для инициации звонка
        function initiateCall(phoneNumber, leadId) {
            // Формируем URL протокола callspire://
            const protocolUrl = `callspire://call?phone=${encodeURIComponent(phoneNumber)}&leadId=${leadId || ''}`;
            
            // Открываем протокол (Windows запустит приложение CallSpire)
            window.location.href = protocolUrl;
        }

        // Добавляем кнопку в меню контакта
        // Это нужно делать через API AmoCRM или через кастомный скрипт
        document.addEventListener('DOMContentLoaded', function() {
            // Ищем меню контакта и добавляем кнопку
            // Конкретная реализация зависит от структуры страницы AmoCRM
        });
    </script>
</body>
</html>
```

## Способ 3: Через API AmoCRM (рекомендуется)

Если у вас есть доступ к API AmoCRM, можно добавить кастомное поле или действие через API.

### Добавление через кастомное поле

1. Создайте кастомное поле типа "URL" или "Текст"
2. В значении поля укажите ссылку вида: `callspire://call?phone={phone}&leadId={lead_id}`
3. AmoCRM автоматически сделает его кликабельным

### Добавление через кастомное действие (если доступно)

В настройках AmoCRM можно добавить кастомное действие, которое будет вызывать протокол `callspire://`.

## Способ 4: Через браузерное расширение (самый надежный)

Создайте расширение для браузера, которое:
1. Находит меню контакта в AmoCRM
2. Добавляет кнопку "CallSpire"
3. При клике формирует URL `callspire://call?phone=...&leadId=...` и открывает его

### Пример расширения (manifest.json)

```json
{
  "manifest_version": 3,
  "name": "CallSpire Integration",
  "version": "1.0",
  "description": "Добавляет кнопку CallSpire в AmoCRM",
  "permissions": [
    "activeTab"
  ],
  "content_scripts": [
    {
      "matches": ["https://*.amocrm.ru/*", "https://*.kommo.com/*"],
      "js": ["content.js"]
    }
  ]
}
```

### content.js

```javascript
// Ждем загрузки страницы
setTimeout(() => {
    // Ищем меню контакта
    const contactMenu = document.querySelector('[data-test-id="contact-menu"]');
    
    if (contactMenu) {
        // Создаем кнопку CallSpire
        const callSpireButton = document.createElement('button');
        callSpireButton.textContent = 'CallSpire';
        callSpireButton.className = 'callspire-integration-button';
        callSpireButton.onclick = function() {
            // Получаем номер телефона и ID лида из страницы
            const phoneNumber = getPhoneNumberFromPage();
            const leadId = getLeadIdFromPage();
            
            // Формируем URL протокола
            const protocolUrl = `callspire://call?phone=${encodeURIComponent(phoneNumber)}&leadId=${leadId || ''}`;
            
            // Открываем протокол
            window.location.href = protocolUrl;
        };
        
        // Добавляем кнопку в меню
        contactMenu.appendChild(callSpireButton);
    }
}, 1000);

function getPhoneNumberFromPage() {
    // Извлекаем номер телефона из страницы AmoCRM
    // Это зависит от структуры страницы AmoCRM
    const phoneElement = document.querySelector('[data-phone]');
    return phoneElement ? phoneElement.getAttribute('data-phone') : '';
}

function getLeadIdFromPage() {
    // Извлекаем ID лида из URL или страницы
    const urlMatch = window.location.href.match(/\/leads\/detail\/(\d+)/);
    return urlMatch ? urlMatch[1] : '';
}
```

## Способ 5: Через настройки интеграций AmoCRM (если есть)

В некоторых версиях AmoCRM можно добавить кастомную интеграцию через:
1. **Настройки** → **Интеграции** → **Добавить интеграцию**
2. Выбрать тип "Кастомная интеграция" или "Внешняя интеграция"
3. Указать:
   - **Название:** CallSpire
   - **URL действия:** `callspire://call?phone={phone}&leadId={lead_id}`
   - **Иконка:** Загрузить иконку CallSpire

## Рекомендация

**Самый простой способ** - создать браузерное расширение, которое будет:
- Автоматически добавлять кнопку в меню контакта
- Работать на всех аккаунтах AmoCRM
- Не требовать изменений в настройках AmoCRM

Если нужна помощь с созданием расширения или виджета, могу помочь с кодом.
