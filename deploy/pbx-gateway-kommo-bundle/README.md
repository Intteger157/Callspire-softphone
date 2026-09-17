# Перенесено в callspire-pbx-gateway

Канонический каталог gateway (Kommo, CDR, workers):

**[`callspire-pbx-gateway/`](../../callspire-pbx-gateway/)**

Скопируйте файлы оттуда на сервер:

```powershell
cd callspire-pbx-gateway
.\copy-to-server.ps1 -Server user@host -GatewayPath /opt/callspire-pbx-gateway
```

Подробности: `callspire-pbx-gateway/DEPLOY_CHECKLIST.ru.md`.
