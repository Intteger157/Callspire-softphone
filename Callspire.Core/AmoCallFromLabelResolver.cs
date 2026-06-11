using System;
using System.Linq;

namespace Softphone
{
    /// <summary>Resolves «Call from …» subtitle for Amo call_out notes (Main: number, Secondary: connection name).</summary>
    public static class AmoCallFromLabelResolver
    {
        public static string? Resolve(CallHistoryItem? call)
        {
            if (call == null || call.IsIncoming)
                return null;

            var slot = CallStatisticsService.GetEffectiveConnectionSlot(call);
            if (slot == CallConnectionSlot.Secondary)
                return GetSecondaryConnectionDisplayName();

            if (slot == CallConnectionSlot.Main && !string.IsNullOrWhiteSpace(call.OutboundCallerId))
                return call.OutboundCallerId.Trim();

            return null;
        }

        public static string? ResolveFromHistory(string phoneNumber, DateTime callTime)
        {
            var history = new CallHistoryService().GetHistory();
            var call = history
                .Where(x => x.PhoneNumber == phoneNumber && Math.Abs((x.CallTime - callTime).TotalSeconds) < 2)
                .OrderByDescending(x => x.CallTime)
                .FirstOrDefault()
                ?? history.Where(x => x.PhoneNumber == phoneNumber).OrderByDescending(x => x.CallTime).FirstOrDefault();
            return Resolve(call);
        }

        public static string GetSecondaryConnectionDisplayName()
        {
            try
            {
                var settings = AppDataHelper.LoadSettingsOrNew();
                if (!string.IsNullOrWhiteSpace(settings.SecondaryConnectionName))
                    return settings.SecondaryConnectionName.Trim();
            }
            catch { }
            return "Secondary";
        }
    }
}
