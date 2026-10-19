using System.Reflection;
using CommonAssets.Scripts.ArtilleryShelling;
using CommonAssets.Scripts.ArtilleryShelling.Client;
using EFT.Ballistics;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Mortar.Patches
{
    internal class ArtilleryDamageSourcePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(ArtilleryProjectileServer), "GetDamageInfo");
        }

        [PatchPostfix]
        private static void Postfix(ref DamageInfo __result, ArtilleryProjectileExplosiveItem ____explosiveItem)
        {
            if (CoopSupport.Enabled)
                __result.SourceId = FragmentAmmoPatch.ResolveTemplate(____explosiveItem.FragmentType);
        }
    }

    internal class ArtilleryClientDamageSourcePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(ArtilleryProjectileClient), "GetDamageInfo");
        }

        [PatchPostfix]
        private static void Postfix(ref DamageInfo __result, ArtilleryProjectileExplosiveItem ____explosiveItem)
        {
            if (CoopSupport.Enabled)
                __result.SourceId = FragmentAmmoPatch.ResolveTemplate(____explosiveItem.FragmentType);
        }
    }
}