using Callspire.UpdateServer.Services;
using Callspire.UpdateServer.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

var dataDir = builder.Configuration["DataDirectory"];
if (string.IsNullOrWhiteSpace(dataDir))
    dataDir = Path.Combine(builder.Environment.ContentRootPath, "data");
else if (!Path.IsPathRooted(dataDir))
    dataDir = Path.Combine(builder.Environment.ContentRootPath, dataDir);

var publicBase = builder.Configuration["PublicBaseUrl"]?.Trim();
if (string.IsNullOrEmpty(publicBase))
    throw new InvalidOperationException("Configure PublicBaseUrl (e.g. https://callspire.update.portalhm.cc).");

var publicBaseIsHttps = publicBase.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

var store = new UpdateFileStore(dataDir, publicBase);
store.EnsureDataDirectory();

var keysDir = Path.Combine(dataDir, "keys");
Directory.CreateDirectory(keysDir);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
    .SetApplicationName("Callspire.UpdateServer");

builder.Services.AddSingleton(store);
builder.Services.AddHttpContextAccessor();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Login";
        o.LogoutPath = "/Login";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        // За двойным nginx часто приходит X-Forwarded-Proto=http; с публичным HTTPS — всегда Secure.
        o.Cookie.SecurePolicy = publicBaseIsHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        o.SlidingExpiration = true;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
    });
builder.Services.AddAuthorization();
builder.Services.AddRazorPages();
builder.Services.AddAntiforgery(o =>
{
    o.Cookie.SameSite = SameSiteMode.Lax;
    // Anti-forgery token cookie should follow the effective request scheme.
    // With reverse proxies and local HTTP hops, forcing Always can throw 500 on /Login.
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Kestrel только на loopback — доверяем заголовкам от локального nginx.
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    // Несколько прокси в цепочке (внешний SSL + внутренний).
    options.ForwardLimit = 10;
});

// Большие .exe при Publish
const long maxUploadBytes = 512L * 1024 * 1024;
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = maxUploadBytes;
    o.ValueLengthLimit = int.MaxValue;
    o.MultipartHeadersLengthLimit = int.MaxValue;
});
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = maxUploadBytes;
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

app.UseForwardedHeaders();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/update.json", (UpdateFileStore s) =>
{
    if (!File.Exists(s.ManifestPath))
        return Results.NotFound();
    return Results.File(s.ManifestPath, "application/json");
});

app.MapGet("/Callspire-setup.exe", (UpdateFileStore s) =>
{
    if (!File.Exists(s.InstallerPath))
        return Results.NotFound();
    return Results.File(s.InstallerPath, "application/octet-stream", fileDownloadName: s.InstallerFileName);
});

app.MapGet("/releases/{id}/installer", (string id, UpdateFileStore s) =>
{
    if (string.IsNullOrWhiteSpace(id) || id.Contains("..") || id.Contains('/') || id.Contains('\\'))
        return Results.BadRequest();

    var path = s.GetArchivedInstallerPath(id);
    if (!File.Exists(path))
        return Results.NotFound();
    return Results.File(path, "application/octet-stream", fileDownloadName: $"{id}-{s.InstallerFileName}");
}).RequireAuthorization();

app.MapGet("/releases/{id}/manifest", (string id, UpdateFileStore s) =>
{
    if (string.IsNullOrWhiteSpace(id) || id.Contains("..") || id.Contains('/') || id.Contains('\\'))
        return Results.BadRequest();

    var path = s.GetArchivedManifestPath(id);
    if (!File.Exists(path))
        return Results.NotFound();
    return Results.File(path, "application/json", fileDownloadName: $"{id}-update.json");
}).RequireAuthorization();

// Публикация вне Razor Pages — minimal API не гоняет тот же antiforgery-фильтр, что ломал POST за прокси.
app.MapPost("/api/publish", async (HttpContext ctx, UpdateFileStore store, CancellationToken ct) =>
{
    IFormCollection form;
    try
    {
        form = await ctx.Request.ReadFormAsync(ct).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        var m = "Form read failed: " + ex.Message;
        if (m.Length > 400) m = m[..400];
        return Results.Redirect("/Dashboard?err=" + Uri.EscapeDataString(m));
    }

    var version = form["DraftVersion"].ToString().Trim();
    if (string.IsNullOrEmpty(version))
        return Results.Redirect("/Dashboard?err=" + Uri.EscapeDataString("Version is required."));

    var notes = form["DraftNotes"].ToString();
    if (string.IsNullOrWhiteSpace(notes))
        notes = null;

    var mandatory = string.Equals(form["DraftMandatory"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
    var installer = form.Files["installer"];

    try
    {
        if (installer is { Length: > 0 })
        {
            await using var stream = installer.OpenReadStream();
            await store.PublishAsync(version, notes, mandatory, stream, null, ct).ConfigureAwait(false);
        }
        else
        {
            await store.PublishAsync(version, notes, mandatory, null, null, ct).ConfigureAwait(false);
        }
    }
    catch (Exception ex)
    {
        var msg = ex.Message;
        if (msg.Length > 400) msg = msg[..400];
        return Results.Redirect("/Dashboard?err=" + Uri.EscapeDataString(msg));
    }

    await store.ClearDraftAsync(ct).ConfigureAwait(false);
    return Results.Redirect("/Dashboard?published=1");
}).RequireAuthorization();

app.MapPost("/api/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
    return Results.Redirect("/Login");
}).RequireAuthorization();

app.MapPost("/api/delete-release", async (HttpContext ctx, UpdateFileStore store, CancellationToken ct) =>
{
    try
    {
        await store.DeleteCurrentAndRollbackAsync(ct).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        var msg = ex.Message;
        if (msg.Length > 400) msg = msg[..400];
        return Results.Redirect("/Dashboard?err=" + Uri.EscapeDataString(msg));
    }

    return Results.Redirect("/Dashboard?deleted=1");
}).RequireAuthorization();

app.MapPost("/api/draft/save", async (HttpContext ctx, UpdateFileStore store, CancellationToken ct) =>
{
    IFormCollection form;
    try
    {
        form = await ctx.Request.ReadFormAsync(ct).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        var m = ex.Message;
        if (m.Length > 300) m = m[..300];
        return Results.Redirect("/Dashboard?err=" + Uri.EscapeDataString(m));
    }

    var version = form["DraftVersion"].ToString();
    var notes = form["DraftNotes"].ToString();
    var mandatory = string.Equals(form["DraftMandatory"].ToString(), "true", StringComparison.OrdinalIgnoreCase);

    await store.SaveDraftAsync(new ReleaseDraft
    {
        Version = string.IsNullOrWhiteSpace(version) ? null : version,
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes,
        Mandatory = mandatory
    }, ct).ConfigureAwait(false);

    return Results.Redirect("/Dashboard?draft=1");
}).RequireAuthorization();

app.MapPost("/api/releases/{id}/notes", async (string id, HttpContext ctx, UpdateFileStore store, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(id) || id.Contains("..") || id.Contains('/') || id.Contains('\\'))
        return Results.BadRequest();

    IFormCollection form;
    try
    {
        form = await ctx.Request.ReadFormAsync(ct).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        var m = ex.Message;
        if (m.Length > 300) m = m[..300];
        return Results.BadRequest(m);
    }

    var notes = form["notes"].ToString();
    try
    {
        await store.UpdateArchivedNotesAsync(id, notes, ct).ConfigureAwait(false);
        return Results.Ok();
    }
    catch (Exception ex)
    {
        var msg = ex.Message;
        if (msg.Length > 400) msg = msg[..400];
        return Results.BadRequest(msg);
    }
}).RequireAuthorization();

app.MapPost("/api/releases/{id}/delete", async (string id, UpdateFileStore store, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(id) || id.Contains("..") || id.Contains('/') || id.Contains('\\'))
        return Results.BadRequest();

    try
    {
        await store.DeleteArchivedReleaseAsync(id, ct).ConfigureAwait(false);
        return Results.Ok();
    }
    catch (Exception ex)
    {
        var msg = ex.Message;
        if (msg.Length > 400) msg = msg[..400];
        return Results.BadRequest(msg);
    }
}).RequireAuthorization();

app.MapRazorPages();

app.MapGet("/", (HttpContext ctx) =>
    ctx.User.Identity?.IsAuthenticated == true
        ? Results.Redirect("/Dashboard")
        : Results.Redirect("/Login"));

app.Run();
