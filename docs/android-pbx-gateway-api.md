
Источник правды на сервере: репозиторий `callspire-pbx-gateway/` (`app.py`, `app_kommo.py`).


---

## Базовые сведения

| Параметр | Значение |
|----------|----------|
| Base URL | `https://<gateway-host>` — URL PBX Gateway (тот же, что в админке / desktop `MikoPbxCdrServiceUrl`) |
| Auth | `Authorization: Bearer <JWT>` на всех `/api/*` (кроме login) |
| Content-Type | `application/json` для POST/PUT с телом |
| TLS | Gateway может быть с self-signed сертификатом — на mobile нужна своя политика (pinning / trust) |

### Что хранить в приложении после логина

| Поле | Пример | Описание |
|------|--------|----------|
| `gatewayUrl` | `https://pbx.example.com:8443` | Базовый URL |
| `jwt` | `eyJ...` | Токен из `/winapp-auth` |
| `extension` | `52678419` | Extension из ответа auth |

---

## 1. Авторизация

### `POST /winapp-auth` — основной способ для мобильного

Логин **extension + SIP-пароль** (как у softphone на desktop).

**Request:**
```http
POST /winapp-auth
Content-Type: application/json

{
  "username": "52678419",
  "password": "<sip_password>"
}
```

**Response `200`:**
```json
{
  "token": "eyJhbGciOiJIUzI1NiIs...",
  "role": "user",
  "extension": "52678419",
  "must_change_password": false
}
```

**Ошибки:** `400` (не extension), `401` (неверный пароль).

---

### `POST /api/v1/auth/login` — альтернатива (email)

Если пользователь заведён в gateway как app user (email + пароль), не extension.

**Request:**
```json
{
  "username": "user@company.com",
  "password": "..."
}
```

**Response `200`:** тот же формат (`token`, `role`, `extension`, …).

На некоторых инсталляциях endpoint защищён service token на уровне reverse proxy — уточнить у админа.

---

### `GET /health` — проверка доступности

Без авторизации. Успех = gateway жив.

```http
GET /health
```

Используется для диагностики «доступен ли URL» перед проверкой JWT (`GET /api/my-callerids`).

---

## 2. CallerID (исходящие номера)

### `GET /api/my-callerids`

Список CallerID, разрешённых для extension из JWT (назначаются в админке PBX Gateway).

**Request:**
```http
GET /api/my-callerids
Authorization: Bearer <JWT>
```

**Response `200`:**
```json
{
  "extension": "52678419",
  "callerids": ["+74951396143", "+441156612688"],
  "callerid_items": [
    { "number": "+74951396143", "name": "Moscow" },
    { "number": "+441156612688", "name": "UK line" }
  ]
}
```

- `callerids` — массив строк (legacy, только номера).
- `callerid_items` — предпочтительный формат (номер + имя для UI).

**Ошибки:** `401` (просрочен JWT), `403` (нет extension в токене).

**UI:** показать picker перед исходящим; выбранный номер передавать в originate как `callerid`.

---

### `POST /api/originate` — исходящий через PBX (AMI)

PBX сначала звонит на extension пользователя, после ответа соединяет с `destination` с выбранным CallerID.

**Request:**
```json
{
  "destination": "+79776104957",
  "callerid": "+74951396143",
  "ring_extension": "52678419"
}
```

| Поле | Обязательно | Описание |
|------|-------------|----------|
| `destination` | да | Номер назначения (E.164 или цифры) |
| `callerid` | да | Выбранный CallerID из `/api/my-callerids` |
| `ring_extension` | нет | Кому звонить первым; по умолчанию extension из JWT |

**Response `200`:**
```json
{
  "success": true,
  "originate_id": "a1b2c3d4e5f6..."
}
```

`originate_id` — корреляция с callback INVITE на softphone (если используется WebRTC/SIP callback).

**Ошибки:** `400`, `502` (AMI originate failed).

#### Нормализация номера на gateway

Gateway приводит destination к E.164:

- `89776104957` (8 + 10 цифр, **российский** мобильный/городской) → `+79776104957`
- `85296172707` (Гонконг +852) → **`+85296172707`** (не `+752…`)
- Номер с `+` в начале **никогда** не конвертируется 8→7
- Пробелы/дефисы удаляются
- Короткие внутренние номера (≤10 цифр без `+`) не меняются

Рекомендация для Android/desktop: та же логика, что в `Callspire.Core/PhoneNumberHelper.cs` / `phone_normalize.py`.

---

## 3. AmoCRM / Kommo (режим Gateway)

**Рекомендуемый режим для Android:** shared Kommo на gateway. Приложение **не** хранит OAuth client_id/secret Kommo — только JWT gateway.

Админ в PBX Gateway:
1. Авторизует Kommo (OAuth) один раз на компанию.
2. Привязывает extension → пользователь Amo (Use shared + dropdown).

### `GET /api/kommo/status`

Проверка: доступен ли модуль Kommo для этого extension.

**Request:**
```http
GET /api/kommo/status
Authorization: Bearer <JWT>
```

**Response `200` (пример):**
```json
{
  "available": true,
  "enabled": true,
  "configured": true,
  "authorized": true,
  "subdomain": "yourcompany",
  "token_expires_at": "2026-07-10T18:00:00+00:00",
  "token_expired": false,
  "excluded": false,
  "offer_gateway": true,
  "kommo_user_id": 12345678,
  "kommo_user_name": "Elena",
  "upload_enabled": true
}
```

| Поле | Смысл для клиента |
|------|-------------------|
| `offer_gateway` | `true` — можно использовать shared Kommo (не local OAuth в приложении) |
| `upload_enabled` | `true` — extension привязан к пользователю Amo, можно `process-call` |
| `excluded` | `true` — extension исключён из gateway Kommo → только local-режим |
| `authorized` | OAuth на gateway выполнен админом |
| `kommo_user_id` / `kommo_user_name` | Mapped user для звонков/записей |

**Когда вызывать:** при старте, при открытии настроек Kommo, после логина.

---

### `GET /api/kommo/session`

OAuth access token компании + mapped user. Нужен для **прямых запросов к Kommo API** (контакты, лиды, карточки) — как desktop `AmoCrmService`.

**Request:**
```http
GET /api/kommo/session
Authorization: Bearer <JWT>
```

Опционально: `?force_refresh=true` — принудительный refresh токена на gateway.

**Response `200`:**
```json
{
  "subdomain": "yourcompany",
  "access_token": "eyJ...",
  "expires_at": "2026-07-10T18:00:00+00:00",
  "account_base_url": "https://yourcompany.kommo.com",
  "kommo_user_id": 12345678,
  "kommo_user_name": "Elena",
  "kommo_user_id_source": "extension_map"
}
```

**Ошибки:**
- `403` — extension в exclude list
- `503` — Kommo не настроен / не authorized / session недоступна

**Дальше:** запросы к Kommo REST API v4:
```
GET {account_base_url}/api/v4/contacts?query=+79776104957
Authorization: Bearer {access_token}
```

---

### `GET /api/kommo/contact?phone=...` — упрощённый поиск контакта

Gateway сам ходит в Kommo; клиенту не нужен `access_token`.

**Request:**
```http
GET /api/kommo/contact?phone=%2B79776104957
Authorization: Bearer <JWT>
```

**Response `200`:** объект с данными контакта для отображения (имя, id — формат см. gateway `KommoCrmClient.lookup_contact_display`).

**Ошибки:** `400` (нет phone), `503` (Kommo недоступен).

Подходит для экрана входящего/исходящего «кто звонит» без полной интеграции Kommo SDK.

---

### `POST /api/kommo/process-call` — постановка задачи после звонка

Gateway: CDR lookup → скачивание записи с MikoPBX → заметка + аудио в Amo.

**Request (gateway recording — запись с PBX, не с телефона):**
```json
{
  "phone": "+79776104957",
  "call_time": "2026-07-10T13:51:39",
  "session_id": "uuid-уникальный-id-звонка",
  "is_incoming": false,
  "duration_seconds": 45,
  "was_answered": true,
  "lead_id": null,
  "client_recording_enabled": false,
  "connection_slot": "main",
  "call_from_label": "+74951396143",
  "call_log": null,
  "enable_recording_upload": true,
  "answer_time": "2026-07-10T13:51:44",
  "call_end_time": "2026-07-10T13:52:29"
}
```

| Поле | Описание |
|------|----------|
| `phone` | Номер абонента |
| `call_time` | ISO/local время начала (как в desktop) |
| `session_id` | Уникальный id сессии звонка в приложении (dedup) |
| `is_incoming` | Входящий / исходящий |
| `was_answered` | Был ли ответ |
| `duration_seconds` | Длительность разговора |
| `lead_id` | Опционально, если звонок из карточки лида |
| `client_recording_enabled` | `false` — запись с PBX; `true` — клиент загрузит файл сам |
| `enable_recording_upload` | `false` — только заметка без аудио |
| `call_from_label` | CallerID / откуда звонили (для исходящих) |

**Response `200`:**
```json
{
  "id": "job-uuid",
  "status": "queued",
  "lead_id": null,
  "contact_id": null,
  "crm_entity": null,
  "upload_source": null,
  "reason": null,
  "created_at": "2026-07-10T13:52:30+00:00",
  "updated_at": "2026-07-10T13:52:30+00:00"
}
```

**Статусы job:** `queued` → `waiting_recording` → `uploaded` | `failed`

**Ошибки:** `403` (`upload_enabled` false), `503` (Kommo module off).

При повторной отправке с тем же dedup key gateway может вернуть уже существующий job.

---

### `GET /api/kommo/process-call/{job_id}` — poll статуса

**Request:**
```http
GET /api/kommo/process-call/abc-123
Authorization: Bearer <JWT>
```

**Response `200`:** тот же формат, что при create (`status`, `lead_id`, `contact_id`, `reason`, …).

**Ошибки:** `404`, `403` (чужой job).

**Рекомендация:** poll каждые 2–5 с до `uploaded` или `failed` (как desktop), таймаут ~2–5 мин.

---

### `PUT /api/kommo/process-call/{job_id}/recording` — локальная запись

Только если при create было `client_recording_enabled: true`.

**Request:**
```http
PUT /api/kommo/process-call/{job_id}/recording
Authorization: Bearer <JWT>
Content-Type: multipart/form-data

file: <recording.wav>
```

**Response `200`:**
```json
{ "ok": true, "path": "/path/on/server/..." }
```

После upload job переходит в `queued` и worker загружает в Amo.

---

### `POST /api/kommo/process-call/retry` — повтор

**Request:** тело как у `process-call` + опционально `job_id`:
```json
{
  "job_id": "existing-job-uuid",
  "phone": "+79776104957",
  "call_time": "2026-07-10T13:51:39",
  "is_incoming": false,
  "duration_seconds": 45,
  "was_answered": true,
  "lead_id": 12345
}
```

---

### `GET /api/kommo/call-attachments?hours=72`

Список недавно загруженных в Amo звонков для extension (история интеграции).

**Response `200`:**
```json
{
  "items": [ ... ]
}
```

---

## 4. Вспомогательные endpoint'ы (опционально)

| Метод | Путь | Назначение |
|-------|------|------------|
| `GET` | `/api/cdr?from=...&to=...&dst=...&limit=50` | CDR пользователя (история, поиск CallerID по звонку) |
| `GET` | `/api/recording?linkedid=...` | Скачать запись PBX |
| `GET` | `/api/sip-auth-failures?ext=...` | Счётчик ошибок SIP-регистрации |
| `GET` | `/api/v1/me` | Профиль (extension, email) |

Параметры CDR:
- `from`, `to` — ISO datetime
- `dst` — фильтр по номеру
- `ext` — только для admin; обычный user видит только свой extension из JWT

---

## 5. Типовые сценарии (flows)

### A. Старт приложения

```
1. POST /winapp-auth          → сохранить JWT + extension
2. GET  /health               → URL доступен
3. GET  /api/my-callerids     → список CallerID для UI
4. GET  /api/kommo/status     → показать/скрыть блок AmoCRM
```

### B. Исходящий с CallerID (originate)

```
1. GET  /api/my-callerids
2. User выбирает callerid
3. POST /api/originate { destination, callerid }
4. Дождаться callback на SIP/WebRTC (если используется)
```

### C. Карточка контакта при звонке (gateway Kommo)

**Вариант 1 — через gateway:**
```
GET /api/kommo/contact?phone=+79776104957
```

**Вариант 2 — как desktop:**
```
GET /api/kommo/session
→ GET {account_base_url}/api/v4/contacts?query=...
```

### D. После звонка — запись в Amo (gateway mode)

```
1. POST /api/kommo/process-call  (client_recording_enabled: false)
2. GET  /api/kommo/process-call/{job_id}  (poll)
3. status == uploaded → показать успех + lead_id/contact_id
4. status == failed   → POST /api/kommo/process-call/retry
```

### E. После звонка — локальная запись с телефона

```
1. POST /api/kommo/process-call  (client_recording_enabled: true)
   → status: waiting_recording
2. PUT  /api/kommo/process-call/{job_id}/recording  (multipart file)
3. Poll GET .../process-call/{job_id}
```

---

## 6. Коды ошибок HTTP

| Код | Типичная причина |
|-----|------------------|
| `400` | Невалидное тело (нет destination, не extension в winapp-auth) |
| `401` | Нет/просрочен JWT |
| `403` | Нет extension, exclude Kommo, upload не включён |
| `404` | Job не найден |
| `502` | AMI originate / upstream PBX |
| `503` | Kommo не authorized, session недоступна, module off |

Тело ошибки FastAPI обычно:
```json
{ "detail": "Human readable message" }
```

---

## 7. Стабильный контракт (важно)

Эти пути и форматы **уже используются production desktop** — **не менять** без версионирования:

- `GET /api/kommo/status`
- `GET /api/kommo/session`
- `POST /api/kommo/process-call`
- `GET /api/kommo/process-call/{job_id}`
- `POST /api/kommo/process-call/retry`
- `PUT /api/kommo/process-call/{job_id}/recording`
- `GET /api/my-callerids`
- `POST /api/originate`
- `POST /winapp-auth`

Новые **опциональные** поля в JSON допустимы (клиент игнорирует неизвестные).

---

## 8. Референс в монорепо (для коллеги)

| Что | Где смотреть |
|-----|----------------|
| HTTP-клиент gateway | `Callspire.Core/MikoPbxCdrService.cs` |
| Модели request/response | там же (`KommoProcessCallRequest`, `KommoGatewayStatus`, …) |
| Логика Kommo на desktop | `Callspire.Desktop/MainWindow.xaml.cs` (поиск `InitializeAmoCrmFromGateway`, `SubmitKommoProcessCall`) |
| Серверные route | `callspire-pbx-gateway/app.py`, `app_kommo.py` |

Android-проект уже ссылается на `Callspire.Core` — можно переиспользовать `MikoPbxCdrService` напрямую, не дублируя HTTP.

---

## 9. Настройки приложения (parity с desktop)

| Ключ | Gateway mode |
|------|----------------|
| Gateway URL | `MikoPbxCdrServiceUrl` |
| JWT | из `/winapp-auth`, хранить зашифрованно |
| Extension | из auth response |
| Kommo включён | `EnableAmoCrmIntegration = true` |
| Источник Kommo | `AmoCrmConnectionSource = "gateway"` |
| Загрузка записей | `AmoCrmRecordingUploadSource = "gateway"` |

---

*Версия документа: 2026-07-10. При изменении API на gateway — обновить этот файл и `MikoPbxCdrService.cs`.*
