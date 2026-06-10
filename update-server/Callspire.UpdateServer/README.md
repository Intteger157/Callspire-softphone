# Callspire Update Server

ASP.NET Core (net8.0) **update server + admin panel** for Callspire Softphone.

It serves two public endpoints that the softphone client consumes:

- `GET /update.json` — update manifest
- `GET /Callspire-setup.exe` — installer download

And it provides an authenticated admin UI:

- `GET /Login` — sign in
- `GET /Dashboard` — publish a new release, manage draft notes
- `GET /ReleaseHistory` — browse archived releases and edit release notes for archived releases

---

## How it works (high level)

### Publish flow

When you press **Publish** in the admin panel:

1. The server optionally stores the uploaded `Callspire-setup.exe`.
2. Computes `sha256` of the installer.
3. Writes `update.json` in `DataDirectory`.
4. Creates an **archive snapshot** of the release:
   - `data/releases/<id>-Callspire-setup.exe`
   - `data/releases/<id>-update.json`
   - Updates `data/releases/index.json` (shown in **Release history**).

### Draft flow

You can edit release notes without losing them:

- **Draft** button saves the current form state to `data/draft.json`
- Opening `/Dashboard` loads `draft.json` back into the form
- After a successful **Publish**, `draft.json` is automatically cleared

### Editing release notes for archived releases

On `/ReleaseHistory`, the **Edit notes** action updates:

- `data/releases/index.json` (for the UI)
- the archived manifest file `data/releases/<id>-update.json`

---

## Configuration

The server reads configuration from `appsettings.json` and/or environment variables.

### Required

- **PublicBaseUrl**: public HTTPS base URL (used to build the installer URL in `update.json`)
  - Example: `https://callspire.update.portalhm.cc`

### Common

- **DataDirectory**: where runtime files are stored
  - Contains: `update.json`, `Callspire-setup.exe`, `draft.json`, `releases/`, `keys/`

### Admin credentials

- `Admin:User` (env: `Admin__User`) — default `admin`
- `Admin:Password` (env: `Admin__Password`) — must be set to a real password

> Recommended: store the password via an **EnvironmentFile** in systemd, not in git.

---

## Local development (Windows)

From the project folder:

```powershell
dotnet restore
dotnet run
```

Then open:

- `https://localhost:5088/Login` (dev profile may use HTTPS depending on launch settings)

Set `PublicBaseUrl` for development in `appsettings.Development.json` if needed.

---

## Build & publish

### Publish on Windows (recommended)

```powershell
cd .\Callspire.UpdateServer
dotnet publish -c Release -o .\publish
```

Copy **all contents** of `publish/` to the server folder used by systemd (example below: `/opt/callspire-update/app`).

---

## Deploy on Ubuntu (systemd + nginx)

The recommended setup is:

**Internet/LAN → nginx (TLS) → nginx (backend :80) → Kestrel (127.0.0.1:5088)**

### 1) Runtime & folders

- Install **ASP.NET Core Runtime 8** (or SDK 8 if you build on the server).
- Create folders:

```bash
sudo mkdir -p /opt/callspire-update/app
sudo mkdir -p /var/lib/callspire-update/data
sudo chown -R www-data:www-data /var/lib/callspire-update/data
```

### 2) Environment file

Copy example and edit:

```bash
sudo cp deploy/callspire-update.env.example /etc/callspire-update.env
sudo nano /etc/callspire-update.env
sudo chmod 600 /etc/callspire-update.env
```

Example contents:

```env
PublicBaseUrl=https://callspire.update.portalhm.cc
DataDirectory=/var/lib/callspire-update/data

Admin__User=admin
Admin__Password=CHANGE_ME_STRONG_PASSWORD
```

### 3) systemd unit

Install the unit:

```bash
sudo cp deploy/callspire-update.service /etc/systemd/system/callspire-update.service
sudo systemctl daemon-reload
sudo systemctl enable --now callspire-update.service
```

Logs:

```bash
sudo journalctl -u callspire-update.service -f
```

### 4) Backend nginx (on the update server host)

Example `/etc/nginx/sites-available/callspire-update`:

```nginx
server {
    listen 80;
    server_name callspire.update.portalhm.cc 192.168.31.244;

    client_max_body_size 512M;

    location / {
        proxy_pass http://127.0.0.1:5088;
        proxy_http_version 1.1;

        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;

        # if you have a TLS reverse proxy in front, forward its proto:
        proxy_set_header X-Forwarded-Proto $http_x_forwarded_proto;

        proxy_connect_timeout 10s;
        proxy_read_timeout 600s;
        proxy_send_timeout 600s;
    }
}
```

Reload:

```bash
sudo nginx -t && sudo systemctl reload nginx
```

### 5) Front TLS proxy nginx (optional, separate machine)

Proxy to the backend nginx on port 80. Make sure to pass:

```nginx
proxy_set_header X-Forwarded-Proto $scheme;
client_max_body_size 512M;
```

---

## Data layout (`DataDirectory`)

Example:

```
data/
  update.json
  Callspire-setup.exe
  draft.json
  keys/                 # DataProtection keys (persistent)
  releases/
    index.json
    20260326-084012-1.1.9-Callspire-setup.exe
    20260326-084012-1.1.9-update.json
```

---

## Security notes

- Do **not** commit real admin passwords to git.
- Keep `Kestrel` bound to `127.0.0.1` and expose only nginx.
- Increase nginx body limits if your installer is large (`client_max_body_size`).

