## Установка на Linux-хосте (Asterisk в Docker)

Предпосылки:
- контейнер называется `asterisk` (или поменяйте `ASTERISK_CONTAINER`)
- `/etc/asterisk` на хосте примонтирован в контейнер как `/etc/asterisk`

### 1) Подготовить include-структуру

На хосте:

```bash
sudo mkdir -p /etc/asterisk/pjsip.d/users
sudo mkdir -p /etc/asterisk/extensions.d
```

Добавьте в конец `/etc/asterisk/pjsip.conf`:

```ini
#include pjsip.d/users/*.conf
```

В `/etc/asterisk/extensions.conf` добавьте include (например, в конец файла или в нужный контекст):

```ini
#include extensions.d/*.conf
```

### 2) Развернуть сервис

Рекомендуемый путь: `/opt/asterisk-webcfg`

```bash
sudo mkdir -p /opt/asterisk-webcfg
sudo rsync -a --delete ./asterisk-webcfg/ /opt/asterisk-webcfg/
cd /opt/asterisk-webcfg

python3 -m venv .venv
. .venv/bin/activate
pip install -r requirements.txt
```

### 3) Права на Docker exec

Вариант A (проще): добавить пользователя сервиса в группу `docker`:

```bash
sudo useradd --system --home /opt/asterisk-webcfg --shell /usr/sbin/nologin asterisk-webcfg || true
sudo usermod -aG docker asterisk-webcfg
```

Вариант B (строже): `sudoers` только на `docker exec ... -rx ...` (можно сделать позже).

### 4) Права на запись в include-каталоги

```bash
sudo chown -R asterisk-webcfg:asterisk-webcfg /etc/asterisk/pjsip.d/users /etc/asterisk/extensions.d
sudo chmod -R 750 /etc/asterisk/pjsip.d/users /etc/asterisk/extensions.d
sudo mkdir -p /var/backups/asterisk-webcfg
sudo chown -R asterisk-webcfg:asterisk-webcfg /var/backups/asterisk-webcfg
sudo chmod -R 750 /var/backups/asterisk-webcfg
```

### 5) systemd

Скопируйте unit:

```bash
sudo cp /opt/asterisk-webcfg/deploy/systemd/asterisk-webcfg.service /etc/systemd/system/asterisk-webcfg.service
sudo nano /etc/systemd/system/asterisk-webcfg.service
```

Поменяйте:
- `ADMIN_PASS=change-me`
- при необходимости `ASTERISK_CONTAINER` и порт

Запуск:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now asterisk-webcfg
sudo systemctl status asterisk-webcfg
```

Проверка:
- `curl -I http://127.0.0.1:8088/` (попросит basic-auth)

### 6) Nginx (опционально)

Файл: `deploy/nginx/asterisk-webcfg.conf`
- добавьте TLS сертификат
- включите allowlist по вашей подсети

