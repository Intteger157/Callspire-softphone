using System;
using System.Threading.Tasks;
using Softphone.Provisioning;

namespace Softphone.AppHost
{
    public sealed partial class DesktopAppController
    {
        /// <summary><c>callspire://provision?token=…&amp;proxy=…</c> from the PBX Gateway admin panel.</summary>
        public void HandleProtocolUrl(string url)
        {
            if (TryExtractCdrAuthToken(url, out var cdrToken))
            {
                HandleCdrAuthToken(cdrToken);
                return;
            }

            if (TryExtractProvisionRequest(url, out var tokenId, out var proxy))
            {
                _ = HandleProvisionAsync(tokenId!, proxy!);
                return;
            }

            string? number = ExtractNumberFromUrl(url);
            if (string.IsNullOrWhiteSpace(number)) return;
            Log($"[Controller] click-to-call: {number}");
            _shell?.BringToForeground();

            if (ViewModel.AnyOnline)
            {
                _ = PlaceCallAsync(number);
                return;
            }

            lock (_pendingClickToCall) _pendingClickToCall.Enqueue(number);
            UiThread.BeginInvoke(() => ViewModel.PhoneNumber = number);
        }

        internal static bool TryExtractProvisionRequest(string url, out string? tokenId, out string? proxy)
        {
            tokenId = null;
            proxy = null;
            if (string.IsNullOrWhiteSpace(url)) return false;
            try
            {
                var uri = new Uri(url);
                if (!string.Equals(uri.Scheme, "callspire", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(uri.Host, "provision", StringComparison.OrdinalIgnoreCase))
                    return false;

                foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = pair.Split('=', 2);
                    if (kv.Length != 2) continue;
                    var key = Uri.UnescapeDataString(kv[0]);
                    var val = Uri.UnescapeDataString(kv[1]);
                    if (key == "token") tokenId = val;
                    else if (key == "proxy") proxy = val;
                }
                return !string.IsNullOrEmpty(tokenId) && !string.IsNullOrEmpty(proxy);
            }
            catch
            {
                return false;
            }
        }

        private async Task HandleProvisionAsync(string tokenId, string proxy)
        {
            Log($"[Controller] provision link (proxy={proxy})");
            UiThread.BeginInvoke(() => _shell?.BringToForeground());

            var result = await ProvisionRedeemService.RedeemAsync(tokenId, proxy).ConfigureAwait(false);
            if (!result.Success || result.Settings == null)
            {
                UiThread.BeginInvoke(() => _shell?.ShowMessage("Callspire — setup link", result.UserMessage ?? "Setup failed."));
                return;
            }

            SaveSettings(result.Settings);
            try { await ReconnectFromSettingsAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log($"[Controller] provision reconnect: {ex.Message}"); }

            UiThread.BeginInvoke(() => _shell?.ShowMessage("Callspire", result.UserMessage ?? "Settings applied."));
        }
    }
}
