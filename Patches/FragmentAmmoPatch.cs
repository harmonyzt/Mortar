using System;
using System.Reflection;
using CommonAssets.Scripts.ArtilleryShelling;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Mortar.Patches
{
    /// <summary>
    /// Since BSG want it the hard way, its CreateFragment() hardcodes ARTILLERY_FRAGMENT_TEMPLATE_ID to
    /// "Ammo" category node (PenetrationPower 0, Damage 0) and not an ammo item.
    /// We want 66ec2aa6daf127599c0c31f1 = shrapnel_mine_om_82, PenetrationPower 17) but the client ignores it
    /// </summary>
    internal class FragmentAmmoPatch : ModulePatch
    {
        private static bool _logged;
        internal const string DefaultTemplate = "66ec2aa6daf127599c0c31f1";

        internal static string ResolveTemplate(string candidate)
        {
            if (candidate == null || candidate.Length != 24)
            {
                return DefaultTemplate;
            }

            foreach (char c in candidate)
            {
                if (!Uri.IsHexDigit(c))
                {
                    return DefaultTemplate;
                }
            }

            ItemFactory factory = Singleton<ItemFactory>.Instance;
            return factory != null && factory.ItemTemplates.TryGetValue(candidate, out ItemTemplate template) &&
                   template is AmmoTemplate ammo && ammo.PenetrationPower > 0
                ? candidate
                : DefaultTemplate;
        }

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(ArtilleryProjectileExplosiveItem), "CreateFragment");
        }

        [PatchPrefix]
        private static bool CreateFragmentPrefix(ArtilleryProjectileExplosiveItem __instance, ref Ammo __result)
        {
            if (!CoopSupport.Enabled)
            {
                return true;
            }

            try
            {
                string templateId = ResolveTemplate(__instance.FragmentType);
                if (string.IsNullOrEmpty(templateId))
                {
                    return true;
                }

                ItemFactory factory = Singleton<ItemFactory>.Instance;
                if (factory == null || !factory.ItemTemplates.ContainsKey(templateId))
                {
                    return true;
                }

                Ammo ammo = factory.CreateItem(MongoID.Generate(), templateId, null) as Ammo;
                if (ammo == null)
                {
                    return true;
                }

                if (!_logged)
                {
                    _logged = true;
                    AmmoTemplate template = factory.ItemTemplates[templateId] as AmmoTemplate;

                    if (template != null && template.PenetrationPower <= 0)
                    {
                        Plugin.LogSource.LogWarning(
                            $"[Mortar] fragment template '{templateId}' has PenetrationPower 0 - shells will deal no body damage.");
                    }
                    else
                    {
                        Plugin.LogSource.LogInfo($"[Mortar] artillery fragments use template '{templateId}'.");
                    }
                }

                __result = ammo;
                return false;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning(
                    $"[Mortar] FragmentAmmoPatch failed, falling back to vanilla: {ex.Message}");

                return true;
            }
        }
    }
}