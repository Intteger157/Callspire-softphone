using Callspire.UpdateServer.Models;
using Callspire.UpdateServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Callspire.UpdateServer.Pages;

[Authorize]
public class ReleaseHistoryModel : PageModel
{
    private readonly UpdateFileStore _store;

    public ReleaseHistoryModel(UpdateFileStore store)
    {
        _store = store;
    }

    public IReadOnlyList<ArchivedReleaseInfo> History { get; set; } = Array.Empty<ArchivedReleaseInfo>();

    public async Task OnGetAsync()
    {
        await _store.RepairCurrentReleaseFromHistoryAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        History = await _store.ReadArchivedReleasesAsync(HttpContext.RequestAborted).ConfigureAwait(false);
    }
}

