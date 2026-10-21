using System;
using BepInEx.Configuration;
using UnityEngine;

namespace Mortar
{
    internal sealed class SirenSoundDefinition
    {
        /// <summary>
        /// Path relative to Resources
        /// </summary>
        public string ResourcePath;

        /// <summary>
        /// Where the sound comes from. Null fallsbacks to the map Origin
        /// </summary>
        public Vector3? Origin;

        /// <summary>
        /// Maximum heard distance in metres. 0 means unlimited
        /// </summary>
        public float MaxDistance;

        /// <summary>
        /// True for positional audio (direction + distance). False keeps a flat, map-wide audio source
        /// </summary>
        public bool Spatial = true;

        public float Volume = 0.8f;

        /// <summary>
        /// Seconds to wait after the trigger before this sound starts. Set 0 to insta play
        /// </summary>
        public float DelaySeconds = 0f;

        public bool Loop;

        /// <summary>
        /// How long a looped sound keeps playing. Ignored if loop = false
        /// </summary>
        public float LoopSeconds = 30f;
    }

    internal sealed class SirenMapDefinition
    {
        public string LocationId;
        public string Name;
        public string ConfigSection;
        public string ConfigKey;
        public string ConfigDescription;
        
        public Vector3 Origin;

        public SirenSoundDefinition[] Sounds;

        internal ConfigEntry<bool> Enabled;

        public bool IsEnabled => Enabled == null || Enabled.Value;
    }

    /// per-map siren configuration
    internal static class SirenRegistry
    {
        internal static readonly SirenMapDefinition[] Maps =
        [
            new()
            {
                LocationId = "RezervBase",
                Name = "Reserve",
                ConfigSection = "Reserve Siren",
                ConfigKey = "Enable Reserve Siren",
                ConfigDescription =
                    "Play Reserve's siren and office alarm when artillery is called.",
                Origin = new Vector3(-32.78f, -5.40f, -3.61f),
                Sounds =
                [
                    new SirenSoundDefinition
                    {
                        ResourcePath = "RezervBase/siren_cut.mp3",
                        Volume = 0.8f,
                        Spatial = false,
                        MaxDistance = 0f,
                    },
                ],
            },
            new()
            {
                LocationId = "shoreline",
                Name = "Shoreline",
                ConfigSection = "Shoreline Siren",
                ConfigKey = "Enable Shoreline distant siren that can be heard from Reserve",
                ConfigDescription =
                    "Play Shoreline's siren (from reserve) when artillery is called.",

                Origin = new Vector3(-132.97f, 27.70f, -417.23f),
                Sounds =
                [
                    new SirenSoundDefinition
                    {
                        ResourcePath = "RezervBase/siren_cut.mp3",
                        Volume = 0.4f,
                        Spatial = true,
                        MaxDistance = 0f,
                    },
                ],
            }
        ];

        internal static void BindConfig(ConfigFile config)
        {
            foreach (SirenMapDefinition map in Maps)
            {
                map.Enabled = config.Bind(map.ConfigSection, map.ConfigKey, true, map.ConfigDescription);
            }
        }

        internal static SirenMapDefinition Find(string locationId)
        {
            if (string.IsNullOrEmpty(locationId))
            {
                return null;
            }

            foreach (SirenMapDefinition map in Maps)
            {
                if (string.Equals(map.LocationId, locationId, StringComparison.OrdinalIgnoreCase))
                {
                    return map;
                }
            }

            return null;
        }
    }
}