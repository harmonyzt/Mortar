using System;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Mortar.Patches
{
    internal class CallStrikeDelayPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(ArtilleryShellingControllerServer),
                nameof(ArtilleryShellingControllerServer.StartEventShelling));
        }

        [PatchPostfix]
        private static void Postfix(Task __result)
        {
            if (CoopSupport.Enabled)
            {
                StrikeService.TrackPending(__result);
            }
        }

        [PatchPrefix]
        private static void Prefix(ArtilleryShellingControllerServer __instance, ref float initShellingTime)
        {
            if (!Plugin.Enable.Value)
            {
                return;
            }

            float delay = Plugin.WarningDelaySeconds.Value;
            if (delay < 0f)
            {
                return;
            }

            // Only touch called strikes (StartShellingPosition passes InitCalledShellingTime);
            if (Math.Abs(initShellingTime - __instance._currentMapConfiguration.InitCalledShellingTime) > 0.01f)
            {
                return;
            }

            initShellingTime = delay;
        }
    }
}