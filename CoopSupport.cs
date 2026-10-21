using EFT;
using UnityEngine;

namespace Mortar
{
    public interface IMortarNetwork
    {
        bool IsClient { get; }
        bool RequestStrike(Vector3 position, string reason, IPlayer source);

        // Host-only so we can tell every client to play the siren for this location
        void BroadcastSiren(string locationId);
    }

    public static class CoopSupport
    {
        public static IMortarNetwork Network { get; set; }
        public static RaidRules HostRules { get; set; }
        public static bool IsClient => Network != null && Network.IsClient;
        public static bool Enabled => Plugin.Enable.Value && (HostRules == null || HostRules.Enabled);
        public static bool WarnEveryone => HostRules?.WarnEveryone ?? Plugin.WarnEveryone.Value;
        public static bool ForceDetector => HostRules?.ForceDetector ?? Plugin.ForceDetectorForLocalPlayer.Value;

        public static void PrepareMap(string locationId) => PrepareMap(locationId, false);

        public static void PrepareMap(string locationId, bool requireClientMap)
        {
            Patches.EnsureMapConfigPatch.Prepare(new[] { locationId }, requireClientMap);
        }

        public static RaidRules GetLocalRules()
        {
            return new RaidRules
            {
                Enabled = Plugin.Enable.Value,
                WarnEveryone = Plugin.WarnEveryone.Value,
                ForceDetector = Plugin.ForceDetectorForLocalPlayer.Value,
                FlareMode = Plugin.FlareTriggerModeConfig.Value,
                ExcludeQuestFlares = Plugin.ExcludeQuestAndExitFlares.Value,
                StrikeAtFlare = Plugin.StrikeAtFlarePosition.Value
            };
        }
    }

    public sealed class RaidRules
    {
        public bool Enabled;
        public bool WarnEveryone;
        public bool ForceDetector;
        public bool AllowHotkeys;
        public EFlareTriggerMode FlareMode;
        public bool ExcludeQuestFlares;
        public bool StrikeAtFlare;
    }
}