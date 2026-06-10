# Asterisk Web Config Wrapper (Docker)

Лёгкая веб‑админка (UI + API) для управления:
- PJSIP пользователями через генерацию include‑файлов в `pjsip.d/users/`
- dialplan фрагментами через `extensions.d/`

Заточено под схему:
- Asterisk работает в контейнере `asterisk`
- хостовый `/etc/asterisk` примонтирован в контейнер как `/etc/asterisk`
- reload выполняется через `docker exec asterisk asterisk -rx "..."`

## Что НЕ делает (сознательно)
- Не редактирует ваш монолитный `pjsip.conf`/`extensions.conf` “как есть”.
  Сервис пишет только свои фрагменты, чтобы не сломать trunk/transport.

## Требования
- Linux host, где запущен Docker контейнер с Asterisk
- Python 3.11+ (можно 3.10, но лучше 3.11)
- доступ пользователя сервиса к записи в `/etc/asterisk/pjsip.d/users` и `/etc/asterisk/extensions.d`
- возможность выполнить `docker exec asterisk ...` (через группу `docker` или `sudoers`)

## Быстрый старт (dev)

```bash
cd asterisk-webcfg
python -m venv .venv
. .venv/bin/activate
pip install -r requirements.txt

export ASTERISK_ETC=/etc/asterisk
export ASTERISK_CONTAINER=asterisk
export WEB_LISTEN=127.0.0.1
export WEB_PORT=8088
export ADMIN_USER=admin
export ADMIN_PASS=admin

python -m uvicorn webcfg.main:app --host "$WEB_LISTEN" --port "$WEB_PORT"
```

Откройте: `http://127.0.0.1:8088`

## Подготовка include‑структуры на Asterisk (один раз)
Сервис ожидает наличие каталогов:
- `/etc/asterisk/pjsip.d/users/`
- `/etc/asterisk/extensions.d/`

И include‑строк:
- в конце `/etc/asterisk/pjsip.conf`:
  - `#include pjsip.d/users/*.conf`
- в `/etc/asterisk/extensions.conf` (в нужном контексте или в конце файла):
  - `#include extensions.d/*.conf`

## Конфигурация (env)
- `ASTERISK_ETC`: путь к папке конфигов на хосте (default: `/etc/asterisk`)
- `ASTERISK_CONTAINER`: имя контейнера (default: `asterisk`)
- `BACKUP_DIR`: куда складывать бэкапы (default: `/var/backups/asterisk-webcfg`)
- `ADMIN_USER`, `ADMIN_PASS`: простая basic‑auth для MVP (default: `admin/admin`)

## Производственный запуск
Смотрите:
- `deploy/systemd/asterisk-webcfg.service`
- `deploy/nginx/asterisk-webcfg.conf`

