using System.Collections.Generic;
using CommonAssets.Scripts.ArtilleryShelling;
using EFT;
using UnityEngine;

namespace Mortar
{
    /// <summary>
    /// ArtilleryShellingMapConfiguration for locations that ship without one
    /// </summary>
    internal static class MapConfigFactory
    {
        private const float GridStepMeters = 10f;

        internal static ArtilleryShellingMapConfiguration Build(string locationId)
        {
            float requestedSize = Plugin.SynthZoneSizeMeters.Value;
            int gridPoints = Mathf.Max(2, Mathf.CeilToInt(requestedSize / GridStepMeters));
            float size = GridStepMeters * gridPoints;

            float calledDelay = Plugin.WarningDelaySeconds.Value >= 0f
                ? Plugin.WarningDelaySeconds.Value
                : 15f;

            ArtilleryShellingZone zone = new ArtilleryShellingZone
            {
                ID = "mortair_" + locationId,
                PointsInShellings = new Vector2(18f, 26f),
                ShellingRounds = Plugin.SynthRounds.Value,
                ShotCount = Plugin.SynthShotsPerRound.Value,
                PauseBetweenRounds = new Vector2(4f, 8f),
                PauseBetweenShots = new Vector2(0.5f, 2f),
                Center = Vector3.zero,
                ExplosionDistanceRange = new Vector2(1f, 1.4f),
                Rotate = 0f,
                GridStep = new Vector2(GridStepMeters, GridStepMeters),
                Points = new Vector2(gridPoints, gridPoints),
                PointsWorldPositions = new Vector3[0],
                PointRadius = Mathf.Max(4f, size / 8f),
                AlarmStages = new[]
                {
                    new AlarmStage { Value = new Vector2(12f, 0.75f) },
                    new AlarmStage { Value = new Vector2(20f, 1.5f) },
                    new AlarmStage { Value = new Vector2(30f, 5f) },
                    new AlarmStage { Value = new Vector2(40f, 10000f) }
                },
                LootTemplateId = 0,
                UsedInPlanedShelling = false,
                UseInCalledShelling = true,
                IsActive = false,
                ZoneBounds = new List<Vector2>()
            };

            // GetNearestBrigade() reads ArtilleryGuns[1] since every brigade needs at least 2 guns.
            ArtilleryBrigade brigade = new ArtilleryBrigade
            {
                ID = 1,
                ArtilleryGuns = new[]
                {
                    new ArtilleryGun { Position = Vector3.zero },
                    new ArtilleryGun { Position = Vector3.zero },
                    new ArtilleryGun { Position = Vector3.zero }
                }
            };

            return new ArtilleryShellingMapConfiguration
            {
                PlanedShellingOn = false,
                InitShellingTimer = Plugin.ScheduledShellingDelaySeconds.Value,
                InitCalledShellingTime = calledDelay,
                BeforeShellingSignalTime = Mathf.Min(8f, calledDelay),
                ShellingCount = 1,
                ZonesInShelling = 1,
                NewZonesForEachShelling = false,
                ShellingZones = new[] { zone },
                Brigades = new[] { brigade },
                PauseBetweenShellings = new Vector2(30f, 60f),
                ArtilleryShellingAirDropSettings = new GlobalConfiguration.ArtilleryShellingAirDropSettings
                {
                    UseAirDrop = false,
                    AirDropTime = 60,
                    AirDropPosition = Vector3.zero,
                    LootTemplateId = null
                }
            };
        }

        /// <summary>
        /// Turn on BSG's planned map-wide shelling for a config
        /// </summary>
        internal static void EnableScheduledShelling(ref ArtilleryShellingMapConfiguration config)
        {
            config.PlanedShellingOn = true;
            config.InitShellingTimer = Plugin.ScheduledShellingDelaySeconds.Value;

            if (config.ShellingCount <= 0)
            {
                config.ShellingCount = 1;
            }

            if (config.ZonesInShelling <= 0)
            {
                config.ZonesInShelling = 1;
            }

            // ArtilleryShellingControllerServer.Init reads AirDropTime before it checks UseAirDrop
            if (config.ArtilleryShellingAirDropSettings == null)
            {
                config.ArtilleryShellingAirDropSettings = new GlobalConfiguration.ArtilleryShellingAirDropSettings
                {
                    UseAirDrop = false,
                    AirDropTime = 60,
                    AirDropPosition = Vector3.zero,
                    LootTemplateId = null
                };
            }

            ArtilleryShellingZone[] zones = config.ShellingZones;
            if (zones == null)
            {
                return;
            }

            // Planned shelling only picks zones with UsedInPlanedShelling && IsActive
            for (int i = 0; i < zones.Length; i++)
            {
                zones[i].UsedInPlanedShelling = true;
                zones[i].IsActive = true;
            }
        }
    }
}
