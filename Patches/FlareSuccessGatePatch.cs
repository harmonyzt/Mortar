using System.Reflection;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Mortar.Patches
{
    // Gates BSG's own flare -> artillery handler
    internal class FlareSuccessGatePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(ArtilleryShellingControllerServer),
                nameof(ArtilleryShellingControllerServer.OnFlareSuccessEvent));
        }

        [PatchPrefix]
        private static bool Prefix()
        {
            if (!Plugin.Enable.Value)
            {
                return true;
            }

            if (CoopSupport.Network != null)
            {
                return false;
            }

            return Plugin.FlareTriggerModeConfig.Value == EFlareTriggerMode.VanillaOnly;
        }
    }
}