using System.Reflection;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Mortar.Patches
{
    internal class MapSirenTriggerPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(ArtilleryShellingControllerServer),
                nameof(ArtilleryShellingControllerServer.AddNotifyShellingZOneToQueue));
        }

        [PatchPostfix]
        private static void Postfix()
        {
            MapSiren.Trigger();
        }
    }
}
