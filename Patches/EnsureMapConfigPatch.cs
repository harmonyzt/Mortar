using System;
using System.Collections.Generic;
using System.Reflection;
using CommonAssets.Scripts.ArtilleryShelling;
using Comfort.Common;
using EFT;
using HarmonyLib;
using Mortair;
using SPT.Reflection.Patching;

namespace Mortar.Patches
{
    /// <summary>
    /// Runs before BaseLocalGame.Run (via LocalGame) reaches the shelling controller creation block and
    /// guarantees GlobalConfiguration.ArtilleryShelling.ArtilleryMapsConfigs contains the current location,
    /// synthesizing a full map config when the map ships without one (Mortar.Enable On Unsupported Maps).
    ///
    /// Two different ids are in play and both must be covered:
    ///   - Run's controller check uses _raidSettings.selectedLocation.Id (LocalRaidSettings.locationLoot)
    ///   - the controllers' own Init() reads GameWorld.LocationId (= Location.Id, RaidSettings.SelectedLocation)
    /// </summary>
    internal class EnsureMapConfigPatch : ModulePatch
    {
        private static readonly string[] SkippedLocations =
        {
            "hideout"
        };

        private static bool _loggedExplosionOverride;
        private static GlobalConfiguration.ArtilleryShellingGlobalSettings _settingsOwner;
        private static GlobalConfiguration.ArtilleryProjectileExplosionParams _originalExplosion;
        private static int _originalMaxStrikes;

        private static readonly Dictionary<string, ArtilleryShellingMapConfiguration> OriginalMaps =
            new Dictionary<string, ArtilleryShellingMapConfiguration>(StringComparer.OrdinalIgnoreCase);

        protected override MethodBase GetTargetMethod()
        {
            // BaseLocalGame<TPlayerOwner>.Run(BotControllerSettings, string, InventoryController)
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                       BindingFlags.DeclaredOnly;
            for (Type type = typeof(LocalGame); type != null; type = type.BaseType)
            {
                foreach (MethodInfo method in type.GetMethods(flags))
                {
                    if (method.Name == "Run" && !method.IsGenericMethodDefinition && method.GetParameters().Length == 3)
                    {
                        return method;
                    }
                }
            }

            return null;
        }

        [PatchPrefix]
        private static void Prefix(object __instance)
        {
            Prepare(ResolveLocationIds(__instance));
        }

        internal static void Prepare(IEnumerable<string> locationIds, bool requireClientMap = false)
        {
            try
            {
                if (!Plugin.Enable.Value)
                {
                    return;
                }

                GlobalConfiguration.ArtilleryShellingGlobalSettings settings =
                    Singleton<GlobalConfiguration>.Instance?.ArtilleryShelling;
                if (settings == null)
                {
                    Plugin.LogSource.LogWarning(
                        "[Mortar] GlobalConfiguration.ArtilleryShelling is null, cannot inject map config.");
                    return;
                }

                if (settings.ArtilleryMapsConfigs == null)
                {
                    settings.ArtilleryMapsConfigs = new Dictionary<string, ArtilleryShellingMapConfiguration>();
                }

                if (!ReferenceEquals(_settingsOwner, settings))
                {
                    _settingsOwner = settings;
                    _originalExplosion = settings.ProjectileExplosionParams;
                    _originalMaxStrikes = settings.MaxCalledShellingCount;
                    OriginalMaps.Clear();
                    StrikeService.ClearSynthesizedMaps();
                }

                settings.ProjectileExplosionParams = _originalExplosion;
                settings.MaxCalledShellingCount = _originalMaxStrikes;
                EnsureGlobalSettings(settings);

                StrikeService.ResetRaidState();
                Plugin.Instance?.PrepareRaid();
                foreach (string locationId in locationIds)
                {
                    if (!string.IsNullOrEmpty(locationId) && Array.IndexOf(SkippedLocations, locationId) < 0)
                        ProcessLocation(settings, locationId, requireClientMap);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[Mortar] EnsureMapConfigPatch error: {ex}");
            }
        }

        private static void ProcessLocation(GlobalConfiguration.ArtilleryShellingGlobalSettings settings,
            string locationId, bool requireClientMap)
        {
            ArtilleryShellingMapConfiguration config;
            if (StrikeService.IsSynthesized(locationId) ||
                !settings.ArtilleryMapsConfigs.TryGetValue(locationId, out config))
            {
                if (!Plugin.EnableOnUnsupportedMaps.Value && !requireClientMap)
                {
                    settings.ArtilleryMapsConfigs.Remove(locationId);

                    return;
                }

                config = MapConfigFactory.Build(locationId);
                StrikeService.MarkSynthesized(locationId);
                Plugin.LogSource.LogInfo($"[Mortar] synthesized artillery config for '{locationId}'.");
            }
            else if (OriginalMaps.TryGetValue(locationId, out ArtilleryShellingMapConfiguration original))
            {
                config = original;
            }
            else
            {
                OriginalMaps.Add(locationId, config);
            }

            // Scheduled flags must not mutate the next raid's original zone array.
            if (config.ShellingZones != null)
            {
                config.ShellingZones = (ArtilleryShellingZone[])config.ShellingZones.Clone();
            }

            if (Plugin.EnableScheduledShelling.Value && !config.PlanedShellingOn)
            {
                MapConfigFactory.EnableScheduledShelling(ref config);
                Plugin.LogSource.LogInfo(
                    $"[Mortar] planned shelling enabled for '{locationId}' (delay {config.InitShellingTimer}s).");
            }

            // Also write back when restoring the original scheduled-shelling flags.
            settings.ArtilleryMapsConfigs[locationId] = config;
        }

        private static void EnsureGlobalSettings(GlobalConfiguration.ArtilleryShellingGlobalSettings settings)
        {
            if (Plugin.UseModExplosionParams.Value || settings.ProjectileExplosionParams == null)
            {
                settings.ProjectileExplosionParams = Plugin.BuildExplosionParams();
                if (!_loggedExplosionOverride)
                {
                    _loggedExplosionOverride = true;
                    Plugin.LogSource.LogInfo("[Mortar] using Mortar explosion params (Explosion.* config).");
                }
            }

            if (Plugin.MaxSimultaneousStrikes.Value > 0)
            {
                settings.MaxCalledShellingCount = Plugin.MaxSimultaneousStrikes.Value;
            }

            if (settings.MaxCalledShellingCount < 1)
            {
                settings.MaxCalledShellingCount = 3;
            }
        }

        private static IEnumerable<string> ResolveLocationIds(object instance)
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // _raidSettings.selectedLocation -> key used by Run's controller-creation check.
                object raidSettings = AccessTools.Field(instance.GetType(), "_raidSettings")?.GetValue(instance);
                object selected = raidSettings != null
                    ? AccessTools.Field(raidSettings.GetType(), "selectedLocation")?.GetValue(raidSettings)
                    : null;
                AddId(ids, ReadLocationId(selected));

                // Location -> key used by GameWorld.LocationId / controller Init().
                object location = AccessTools.Property(instance.GetType(), "Location")?.GetValue(instance);
                AddId(ids, ReadLocationId(location));
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[Mortar] could not resolve raid location id: {ex.Message}");
            }

            return ids;
        }

        private static void AddId(HashSet<string> ids, string id)
        {
            if (string.IsNullOrEmpty(id) || Array.IndexOf(SkippedLocations, id) >= 0)
            {
                return;
            }

            ids.Add(id);
        }

        private static string ReadLocationId(object location)
        {
            if (location == null)
            {
                return null;
            }

            PropertyInfo property = AccessTools.Property(location.GetType(), "Id");
            if (property != null && property.GetValue(location) is string id)
            {
                return id;
            }

            FieldInfo field = AccessTools.Field(location.GetType(), "Id") ??
                              AccessTools.Field(location.GetType(), "_Id");
            return field?.GetValue(location) as string;
        }
    }
}