using System;

namespace Softphone
{
    /// <summary>Result of checking one or more update sources.</summary>
    public sealed class UpdateCheckResult
    {
        public UpdateInfo? Server { get; init; }
        public UpdateInfo? GitHub { get; init; }

        public bool HasUpgrade(string currentVersion)
        {
            return IsNewerThanCurrent(Server, currentVersion)
                || IsNewerThanCurrent(GitHub, currentVersion);
        }

        public static bool IsNewerThanCurrent(UpdateInfo? info, string currentVersion)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.Version))
                return false;
            return UpdateService.CompareVersions(currentVersion, info.Version) < 0;
        }

        /// <summary>Highest version among sources that is newer than <paramref name="currentVersion"/>.</summary>
        public UpdateInfo? NewestAvailable(string currentVersion)
        {
            UpdateInfo? best = null;
            foreach (var candidate in new[] { Server, GitHub })
            {
                if (!IsNewerThanCurrent(candidate, currentVersion))
                    continue;
                if (best == null
                    || UpdateService.CompareVersions(best.Version, candidate!.Version) < 0)
                {
                    best = candidate;
                }
            }
            return best;
        }
    }
}
