using CommonAssets.Scripts.ArtilleryShelling.Client;
using EFT;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using HarmonyLib;

namespace Mortar.Fika
{
    [HarmonyPatch(typeof(BaseGameController), "InitShellingController")]
    internal static class MapSetupPatch
    {
        private static void Prefix(GameWorld gameWorld, JsonType.LocationSettings.Location location)
        {
            // Fika doesn't use LocalGame.Run, which the base mod patches for solo raids
            CoopSupport.HostRules = null;
            CoopSupport.PrepareMap(gameWorld.LocationId, FikaBackendUtils.IsClient);
            if (location.Id != gameWorld.LocationId)
            {
                CoopSupport.PrepareMap(location.Id, FikaBackendUtils.IsClient);
            }

            SyncPlugin.Instance.BeginRaid();
        }

        private static void Postfix() => SyncPlugin.Instance.HostReady();
    }

    [HarmonyPatch(typeof(ObservedPlayer), "InitObservedPlayer")]
    internal static class PlayerReadyPatch
    {
        private static void Postfix(ObservedPlayer __instance) => SyncPlugin.Instance.ApplyPlayerState(__instance);
    }

    [HarmonyPatch(typeof(ArtilleryShellingControllerClient), "Init")]
    internal static class ClientReadyPatch
    {
        private static void Postfix() => SyncPlugin.Instance.ClientReady();
    }

    [HarmonyPatch(typeof(ArtilleryClientProjectilesPool), "GetProjectile")]
    internal static class ProjectileSettingsPatch
    {
        private static void Postfix(ArtilleryProjectileClient __result)
        {
            // The native pool prewarms before the client's settings handshake completes
            if (CoopSupport.Enabled)
            {
                __result.SetExplosiveItemParams(Comfort.Common.Singleton<GlobalConfiguration>.Instance.ArtilleryShelling
                    .ProjectileExplosionParams);
            }
        }
    }

    [HarmonyPatch(typeof(ArtilleryShellingControllerServer), "HasDetector")]
    internal static class HumanDetectorPatch
    {
        private static bool Prefix(Player player, ref bool __result)
        {
            if (CoopSupport.Enabled && CoopSupport.ForceDetector && FikaBackendUtils.IsServer
                && player is FikaPlayer && !player.IsAI)
            {
                __result = true;

                return false;
            }

            return true;
        }
    }
}