using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.GlobalEvents;
using EFT.InventoryLogic;
using Mortair;
using Mortar.Patches;
using UnityEngine;

namespace Mortar
{
    public enum EFlareTriggerMode
    {
        Off,
        VanillaOnly,
        AnyFlare
    }

    // public enum EHotkeyTarget
    // {
    //     Player,
    //     Crosshair
    // }

    [BepInPlugin("com.harmonyzt.mortar", "Mortar Strikes", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource LogSource;
        internal static Plugin Instance;

        // General
        internal static ConfigEntry<bool> Enable;
        internal static ConfigEntry<bool> EnableOnUnsupportedMaps;
        internal static ConfigEntry<float> StrikeCooldownSeconds;

        // Flare
        internal static ConfigEntry<EFlareTriggerMode> FlareTriggerModeConfig;
        internal static ConfigEntry<bool> ExcludeQuestAndExitFlares;
        internal static ConfigEntry<bool> StrikeAtFlarePosition;

        // Hotkey (debug)
        // internal static ConfigEntry<bool> EnableHotkeyStrike;
        // internal static ConfigEntry<KeyboardShortcut> Hotkey;
        // internal static ConfigEntry<EHotkeyTarget> HotkeyTarget;
        // internal static ConfigEntry<float> CrosshairMaxDistance;

        // Ambient strikes
        internal static ConfigEntry<bool> EnableRandomAmbientStrikes;
        internal static ConfigEntry<float> RandomStrikeMinSeconds;
        internal static ConfigEntry<float> RandomStrikeMaxSeconds;
        internal static ConfigEntry<float> RandomStrikeRadius;
        internal static ConfigEntry<float> AmbientStrikeChance;
        internal static ConfigEntry<string> MapAmbientChances;

        // Warning / notifications
        internal static ConfigEntry<float> WarningDelaySeconds;
        internal static ConfigEntry<bool> WarnEveryone;
        internal static ConfigEntry<bool> ForceDetectorForLocalPlayer;
        internal static ConfigEntry<string> CustomWarningText;
        internal static ConfigEntry<string> CustomBusyText;
        internal static ConfigEntry<bool> EnableEyeInteraction;
        // internal static ConfigEntry<EyeProbeMode> EyeProbe;

        // Scheduled (planned) shelling
        internal static ConfigEntry<bool> EnableScheduledShelling;
        internal static ConfigEntry<float> ScheduledShellingDelaySeconds;

        // Synthesized map config
        internal static ConfigEntry<float> SynthZoneSizeMeters;
        internal static ConfigEntry<int> SynthRounds;
        internal static ConfigEntry<int> SynthShotsPerRound;
        internal static ConfigEntry<float> SynthGunDistance;

        // Explosion / lethality (ProjectileExplosionParams)
        internal static ConfigEntry<bool> UseModExplosionParams;
        internal static ConfigEntry<float> MinExplosionDistance;
        internal static ConfigEntry<float> MaxExplosionDistance;
        internal static ConfigEntry<float> FragmentStrength;
        internal static ConfigEntry<int> FragmentCount;
        internal static ConfigEntry<float> FragmentArmorDamage;
        internal static ConfigEntry<float> FragmentPenetrationPower;
        internal static ConfigEntry<float> FragmentStaminaBurnRate;
        internal static ConfigEntry<float> ArmorDamageNearDistance;
        internal static ConfigEntry<float> ArmorDamageFarDistance;
        internal static ConfigEntry<float> ArmorDamageBonus;
        internal static ConfigEntry<float> ContusionNearDistance;
        internal static ConfigEntry<float> ContusionFarDistance;
        internal static ConfigEntry<float> ContusionDuration;
        internal static ConfigEntry<float> BlindnessNearDistance;
        internal static ConfigEntry<float> BlindnessFarDistance;
        internal static ConfigEntry<float> BlindnessDuration;
        internal static ConfigEntry<float> DirectionalDamageAngle;
        internal static ConfigEntry<float> DirectionalDamageMultiplier;
        internal static ConfigEntry<int> MaxSimultaneousStrikes;

        private Action _unsubscribeFlare;
        private bool _raidActive;
        private float _nextAmbientStrikeTime;

        internal void PrepareRaid()
        {
            _raidActive = false;
            EyeWarning.Reset();
        }

        private void Awake()
        {
            Instance = this;
            LogSource = Logger;

            BindConfig();

            new EnsureMapConfigPatch().Enable();
            new FlareSuccessGatePatch().Enable();
            new CallStrikeDelayPatch().Enable();
            new ShellingWarningPatch().Enable();
            new ShellingBusyPatch().Enable();
            new ShellingNotifyEveryonePatch().Enable();
            new ForceDetectorPatch().Enable();
            new FragmentAmmoPatch().Enable();
            new ArtilleryDamageSourcePatch().Enable();
            new ArtilleryClientDamageSourcePatch().Enable();
            new EyeGripPatch().Enable();
            new EyeGripResetPatch().Enable();
            new EyeLeftArmBlendPatch().Enable();
            new EyeSwitchSoundPatch().Enable();
            new EyeUseCompletePatch().Enable();
            //new EyeProbeSpawnPatch().Enable();
            new EyeAlarmPatch().Enable();

            EnsureFlareSubscription();

            Logger.LogInfo("[Mortar] Loaded!");
        }

        private void OnDestroy()
        {
            _unsubscribeFlare?.Invoke();
            _unsubscribeFlare = null;
            EyeWarning.Reset();
        }

        private void BindConfig()
        {
            Enable = Config.Bind("General", "Enable", true,
                "Really? Do you need a description for that?");

            EnableOnUnsupportedMaps = Config.Bind("General", "Enable On Unsupported Maps", true,
                "Make an artillery map config for maps that have no ArtilleryShelling config (Factory, Laboratory) so mortars work there too.");

            FlareTriggerModeConfig = Config.Bind("Flare", "Flare Trigger Mode", EFlareTriggerMode.VanillaOnly,
                "Off: flares never call mortars. VanillaOnly: only the real Signal flare (Blue) works, handled by BSG. AnyFlare: any signal flare calls a strike.");

            ExcludeQuestAndExitFlares = Config.Bind("Flare", "Exclude Quest And Exit Flares", true,
                "In AnyFlare mode, ignore Quest / Exit / Airdrop flares so story and extract flares keep their vanilla meaning.");

            StrikeAtFlarePosition = Config.Bind("Flare", "Strike At Flare Position", false,
                "False sends mortar to your position, True send where the flare is hanging in the air.");

            StrikeCooldownSeconds = Config.Bind("Hotkey", "Strike Cooldown Seconds", 30f,
                new ConfigDescription("Minimum seconds between two mod-triggered strikes.",
                    new AcceptableValueRange<float>(10f, 600f)));

            EnableRandomAmbientStrikes = Config.Bind("Ambient", "Enable Random Ambient Strikes", false,
                "Periodically shell a random spot near you.");

            RandomStrikeMinSeconds = Config.Bind("Ambient", "Interval Min Seconds", 1800f,
                new ConfigDescription("Minimum seconds between ambient strikes.",
                    new AcceptableValueRange<float>(10f, 3600f)));

            RandomStrikeMaxSeconds = Config.Bind("Ambient", "Interval Max Seconds", 3600f,
                new ConfigDescription("Maximum seconds between ambient strikes.",
                    new AcceptableValueRange<float>(10f, 3600f)));

            RandomStrikeRadius = Config.Bind("Ambient", "Strike Radius Meters", 50f,
                new ConfigDescription("How far around you an ambient strike may land.",
                    new AcceptableValueRange<float>(10f, 300f)));

            AmbientStrikeChance = Config.Bind("Ambient", "Ambient Strike Chance", 0.25f,
                new ConfigDescription(
                    "Probability (0..1) that an ambient strike actually happens when the random interval elapses. Map Ambient Chances can override this per map.",
                    new AcceptableValueRange<float>(0f, 1f)));

            MapAmbientChances = Config.Bind("Ambient", "Map Ambient Chances", "bigmap=0.25;lighthouse=0.10;interchange=0.10;Sandbox=0.05;Sandbox_high=0.10;factory4_day=0;factory4_night=0;RezervBase=0.25;shoreline=0.25;woods=0.30;TarkovStreets=0.20;",
                "Per-map chance overrides as locationId=chance pairs separated by ';' (chance 0..1, e.g. bigmap=0.25;lighthouse=0.5). maps not listed fall back to Ambient Strike Chance.");

            WarningDelaySeconds = Config.Bind("Warning", "Warning Delay Seconds", 10f,
                new ConfigDescription(
                    "Seconds between calling a strike and the shells landing (the warning shows BeforeShellingSignalTime seconds earlier). Negative keeps the map default.",
                    new AcceptableValueRange<float>(-1f, 120f)));

            WarnEveryone = Config.Bind("Warning", "Warn Me Even Without Detector", false,
                "Send the warning notification to you even if you are not carrying The Eye.");

            ForceDetectorForLocalPlayer = Config.Bind("Warning", "Detector Alarm For Local Player", false,
                "Treat yourself as if you carry The Eye, so you also get the proximity alarm near a strike zone.");

            CustomWarningText = Config.Bind("Warning", "Warning Text",
                "WARNING! Artillery strike inbound on your position! Seek nearest bunker!",
                "Warning notification text");

            CustomBusyText = Config.Bind("Warning", "Busy Text",
                "Artillery unavailable: another strike is already in progress.",
                "Notification shown when a strike request is rejected because MaxCalledShellingCount is reached.");

            EnableEyeInteraction = Config.Bind("Warning", "Hold The Eye On Warning", true,
                "When carrying The Eye, your PMC will interrupt you and pick it up in his hands so you'd have to disable the alarm.");

            // EyeProbe = Config.Bind("Debug", "Eye Animation Comparison", EyeProbeMode.Off,
            //     "log Eye/jammer poses and bypass left-arm overrides");

            EnableScheduledShelling = Config.Bind("Scheduled", "Enable Scheduled Shelling", false,
                "Turn on BSGs planned map-wide shelling (PlanedShellingOn) for the current raids config. Off by default.");

            ScheduledShellingDelaySeconds = Config.Bind("Scheduled", "Scheduled Delay Seconds", 60f,
                new ConfigDescription(
                    "Seconds after raid start before the planned shelling begins (InitShellingTimer).",
                    new AcceptableValueRange<float>(5f, 600f)));

            SynthZoneSizeMeters = Config.Bind("Synthesized", "Zone Size Meters", 80f,
                new ConfigDescription("Edge length of the synthesized impact grid on maps without artillery config.",
                    new AcceptableValueRange<float>(20f, 200f)));

            SynthRounds = Config.Bind("Synthesized", "Rounds Per Strike", 4,
                new ConfigDescription("Volleys per synthesized strike.", new AcceptableValueRange<int>(1, 20)));

            SynthShotsPerRound = Config.Bind("Synthesized", "Shots Per Round", 5,
                new ConfigDescription("Shells per volley on synthesized maps.", new AcceptableValueRange<int>(1, 30)));

            SynthGunDistance = Config.Bind("Synthesized", "Gun Distance Meters", 250f,
                new ConfigDescription(
                    "How far from the target the synthesized mortar batteries sit (repositioned on every strike).",
                    new AcceptableValueRange<float>(50f, 1000f)));

            UseModExplosionParams = Config.Bind("Explosion", "Use Mortar Explosion Params", true,
                new ConfigDescription(
                    "Overwrite the ProjectileExplosionParams values below whenever a raid starts. Off = keep the server's values (the values below are then only used if the server sent none)."));

            MinExplosionDistance = Config.Bind("Explosion", "Full Damage Distance", 1.7f,
                new ConfigDescription(
                    "Distance (m) from the impact at which a fragment deals full Strength damage; damage falls off linearly to zero at No Damage Distance.",
                    new AcceptableValueRange<float>(0.1f, 50f)));

            MaxExplosionDistance = Config.Bind("Explosion", "No Damage Distance", 15f,
                new ConfigDescription("Distance (m) beyond which fragments deal no damage at all.",
                    new AcceptableValueRange<float>(1f, 150f)));

            FragmentStrength = Config.Bind("Explosion", "Fragment Strength", 38f,
                new ConfigDescription("Base damage of a fragment at Full Damage Distance.",
                    new AcceptableValueRange<float>(1f, 500f)));

            FragmentCount = Config.Bind("Explosion", "Fragments Per Shell", 40,
                new ConfigDescription("Number of fragments simulated per shell impact.",
                    new AcceptableValueRange<int>(1, 100)));

            FragmentArmorDamage = Config.Bind("Explosion", "Fragment Armor Damage", 33f,
                new ConfigDescription("Armor damage per fragment in percent.",
                    new AcceptableValueRange<float>(0f, 100f)));

            FragmentPenetrationPower = Config.Bind("Explosion", "Fragment Penetration Power", 2f,
                new ConfigDescription("Penetration power of a fragment.",
                    new AcceptableValueRange<float>(0f, 10f)));

            FragmentStaminaBurnRate = Config.Bind("Explosion", "Fragment Stamina Burn", 0.05f,
                new ConfigDescription("Stamina burned per fragment hit.",
                    new AcceptableValueRange<float>(0f, 5f)));

            ArmorDamageNearDistance = Config.Bind("Explosion", "Armor Bonus Near Distance", 3f,
                new ConfigDescription(
                    "Within this distance (m) a fragment applies the full Armor Bonus Damage to plates.",
                    new AcceptableValueRange<float>(0f, 50f)));

            ArmorDamageFarDistance = Config.Bind("Explosion", "Armor Bonus Far Distance", 10f,
                new ConfigDescription("Beyond this distance (m) the armor bonus fades to zero, linearly.",
                    new AcceptableValueRange<float>(0f, 150f)));

            ArmorDamageBonus = Config.Bind("Explosion", "Armor Bonus Damage", 33f,
                new ConfigDescription("Flat armor damage added at the near distance.",
                    new AcceptableValueRange<float>(0f, 200f)));

            ContusionNearDistance = Config.Bind("Explosion", "Contusion Near Distance", 6f,
                new ConfigDescription(
                    "Within this distance (m) the shell applies full contusion (ear-ringing stagger). Is not recommended to tweak if you have Bring Back Concussion mod.",
                    new AcceptableValueRange<float>(0f, 50f)));

            ContusionFarDistance = Config.Bind("Explosion", "Contusion Far Distance", 18f,
                new ConfigDescription(
                    "Beyond this distance (m) there is no contusion at all; it fades linearly. Is not recommended to tweak if you have Bring Back Concussion mod.",
                    new AcceptableValueRange<float>(0f, 150f)));

            ContusionDuration = Config.Bind("Explosion", "Contusion Duration", 16f,
                new ConfigDescription(
                    "Maximum contusion duration in seconds at the near distance; needs > 2s to apply at all. Is not recommended to tweak if you have Bring Back Concussion mod.",
                    new AcceptableValueRange<float>(0f, 120f)));

            BlindnessNearDistance = Config.Bind("Explosion", "Blindness Near Distance", 0f,
                new ConfigDescription(
                    "Flash/stun near distance in meters. Vanilla artillery uses no flash at all. Is not recommended to tweak if you have Bring Back Concussion mod.",
                    new AcceptableValueRange<float>(0f, 50f)));

            BlindnessFarDistance = Config.Bind("Explosion", "Blindness Far Distance", 0f,
                new ConfigDescription(
                    "Flash/stun far distance in meters. Is not recommended to tweak if you have Bring Back Concussion mod.",
                    new AcceptableValueRange<float>(0f, 150f)));

            BlindnessDuration = Config.Bind("Explosion", "Blindness Duration", 0f,
                new ConfigDescription(
                    "Flash/stun duration in seconds at the near distance. Is not recommended to tweak if you have Bring Back Concussion mod.",
                    new AcceptableValueRange<float>(0f, 60f)));

            DirectionalDamageAngle = Config.Bind("Explosion", "Directional Damage Angle", 0f,
                new ConfigDescription(
                    "Cone angle in degrees in front of the blast where Directional Damage Multiplier applies. 0 = off (vanilla).",
                    new AcceptableValueRange<float>(0f, 360f)));

            DirectionalDamageMultiplier = Config.Bind("Explosion", "Directional Damage Multiplier", 0f,
                new ConfigDescription(
                    "Damage multiplier inside the directional cone. 0 or with angle 0 = off.",
                    new AcceptableValueRange<float>(0f, 10f)));

            MaxSimultaneousStrikes = Config.Bind("Explosion", "Max Simultaneous Strikes", 3,
                new ConfigDescription(
                    "Global: how many called strikes may be in the warning/impact phase at once. 0 = keep the server's value.",
                    new AcceptableValueRange<int>(0, 20)));
        }

        internal static GlobalConfiguration.ArtilleryProjectileExplosionParams BuildExplosionParams()
        {
            return new GlobalConfiguration.ArtilleryProjectileExplosionParams
            {
                MinExplosionDistance = MinExplosionDistance.Value,
                MaxExplosionDistance = MaxExplosionDistance.Value,
                Strength = FragmentStrength.Value,
                FragmentsCount = FragmentCount.Value,
                ArmorDamage = FragmentArmorDamage.Value,
                PenetrationPower = FragmentPenetrationPower.Value,
                StaminaBurnRate = FragmentStaminaBurnRate.Value,
                ArmorDistanceDistanceDamage = new Vector3(ArmorDamageNearDistance.Value, ArmorDamageFarDistance.Value,
                    ArmorDamageBonus.Value),
                Contusion = new Vector3(ContusionNearDistance.Value, ContusionFarDistance.Value,
                    ContusionDuration.Value),
                Blindness = new Vector3(BlindnessNearDistance.Value, BlindnessFarDistance.Value,
                    BlindnessDuration.Value),
                DirectionalDamageAngle = DirectionalDamageAngle.Value,
                DirectionalDamageMultiplier = DirectionalDamageMultiplier.Value,
                DeadlyDistance = 1.7f,
                FragmentType = FragmentAmmoPatch.ResolveTemplate("66ec2aa6daf127599c0c31f1")
            };
        }

        private void EnsureFlareSubscription()
        {
            if (_unsubscribeFlare != null)
            {
                return;
            }

            try
            {
                _unsubscribeFlare = GlobalEventsController.Instance.SubscribeOnEvent<FlareSuccessEvent>(OnFlareSuccess);
                Logger.LogInfo("[Mortar] subscribed to FlareSuccessEvent.");
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Mortar] could not subscribe to FlareSuccessEvent: {ex}");
            }
        }

        private void OnFlareSuccess(FlareSuccessEvent flare)
        {
            try
            {
                if (!CoopSupport.Enabled)
                {
                    return;
                }

                EFlareTriggerMode mode = CoopSupport.HostRules?.FlareMode ?? FlareTriggerModeConfig.Value;
                if (mode == EFlareTriggerMode.Off ||
                    (mode == EFlareTriggerMode.VanillaOnly && CoopSupport.Network == null))
                {
                    // VanillaOnly is handled by BSG's own ArtilleryShellingControllerServer.OnFlareSuccessEvent.
                    return;
                }

                AmmoTemplate template = flare.AmmoTemplate;
                if (template == null)
                {
                    return;
                }

                bool isArtilleryFlare = template.ContainsType(FlareEventType.CallArtilleryOnMyself);
                if (mode == EFlareTriggerMode.VanillaOnly && !isArtilleryFlare)
                {
                    return;
                }

                if (CoopSupport.Network != null && (flare.FiredPlayer == null || !flare.FiredPlayer.IsYourPlayer))
                {
                    return;
                }

                if (!isArtilleryFlare && (CoopSupport.HostRules?.ExcludeQuestFlares ?? ExcludeQuestAndExitFlares.Value))
                {
                    if (template.ContainsType(FlareEventType.Quest)
                        || template.ContainsType(FlareEventType.ExitActivate)
                        || template.ContainsType(FlareEventType.Airdrop))
                    {
                        return;
                    }
                }

                if (flare.FiredPlayer == null || flare.FiredPlayer.PlayerBones == null)
                {
                    return;
                }

                Vector3 target = (CoopSupport.HostRules?.StrikeAtFlare ?? StrikeAtFlarePosition.Value)
                    ? flare._flarePosition
                    : flare.FiredPlayer.Transform.position;

                StrikeService.TryCallStrike(target, "flare", flare.FiredPlayer);
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Mortar] OnFlareSuccess error: {ex}");
            }
        }

        private void Update()
        {
            try
            {
                EyeWarning.Update();
                if (!CoopSupport.Enabled)
                {
                    return;
                }

                ArtilleryShellingControllerServer controller = ArtilleryShellingControllerServer.Instance;
                GameWorld world = Singleton<GameWorld>.Instance;
                if (world == null || world.ClientShellingController == null)
                {
                    _raidActive = false;
                    return;
                }

                if (!_raidActive)
                {
                    _raidActive = true;
                    float min = Mathf.Min(RandomStrikeMinSeconds.Value, RandomStrikeMaxSeconds.Value);
                    float max = Mathf.Max(RandomStrikeMinSeconds.Value, RandomStrikeMaxSeconds.Value);
                    _nextAmbientStrikeTime = Time.time + UnityEngine.Random.Range(min, max);
                }

                // if (EnableHotkeyStrike.Value && (CoopSupport.HostRules?.AllowHotkeys ?? true) && Hotkey.Value.IsDown())
                // {
                //     HandleHotkey(controller);
                // }

                // Map specific chances
                if (!CoopSupport.IsClient && controller != null && EnableRandomAmbientStrikes.Value &&
                    Time.time >= _nextAmbientStrikeTime)
                {
                    float min = Mathf.Min(RandomStrikeMinSeconds.Value, RandomStrikeMaxSeconds.Value);
                    float max = Mathf.Max(RandomStrikeMinSeconds.Value, RandomStrikeMaxSeconds.Value);
                    _nextAmbientStrikeTime = Time.time + UnityEngine.Random.Range(min, max);

                    float chance = GetAmbientChance(world.LocationId);
                    if (chance >= 1f || (chance > 0f && UnityEngine.Random.value <= chance))
                    {
                        HandleAmbient(controller);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Mortar] Update error: {ex}");
            }
        }

        private void HandleAmbient(ArtilleryShellingControllerServer controller)
        {
            Player player = controller._gameWorld?.MainPlayer;
            if (player == null && controller._gameWorld != null)
            {
                foreach (Player candidate in controller._gameWorld.AllAlivePlayersList)
                    if (!candidate.IsAI && candidate.HealthController.IsAlive)
                    {
                        player = candidate;
                        break;
                    }
            }

            if (player == null || !player.HealthController.IsAlive)
            {
                return;
            }

            float radius = RandomStrikeRadius.Value;
            Vector3 offset = new Vector3(UnityEngine.Random.Range(-radius, radius), 0f,
                UnityEngine.Random.Range(-radius, radius));
            Vector3 target = player.Position + offset;

            if (Physics.Raycast(target + Vector3.up * 300f, Vector3.down, out RaycastHit hit, 400f,
                    EFTHardSettings.Instance.MOVEMENT_MASK))
            {
                target = hit.point;
            }
            else
            {
                return;
            }

            StrikeService.TryCallStrike(target, "ambient", player);
        }

        private static Dictionary<string, float> _parsedMapAmbientChances;
        private static string _parsedMapAmbientChancesRaw;

        /// <summary>
        /// Probability (0..1) that an ambient strike fires when the interval elapses on this map.
        /// Per-map entries in Map Ambient Chances override Ambient Strike Chance
        /// </summary>
        internal static float GetAmbientChance(string locationId)
        {
            float chance = AmbientStrikeChance.Value;

            string raw = MapAmbientChances.Value;
            if (!string.IsNullOrEmpty(raw))
            {
                if (!string.Equals(raw, _parsedMapAmbientChancesRaw, StringComparison.Ordinal))
                {
                    _parsedMapAmbientChances = ParseMapAmbientChances(raw);
                    _parsedMapAmbientChancesRaw = raw;
                }

                if (!string.IsNullOrEmpty(locationId) &&
                    _parsedMapAmbientChances.TryGetValue(locationId, out float mapChance))
                {
                    chance = mapChance;
                }
            }

            return Mathf.Clamp01(chance);
        }

        private static Dictionary<string, float> ParseMapAmbientChances(string raw)
        {
            Dictionary<string, float> parsed = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            string[] entries = raw.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < entries.Length; i++)
            {
                string entry = entries[i].Trim();
                int separator = entry.IndexOfAny(new[] { '=', ':' });
                if (separator <= 0)
                {
                    continue;
                }

                string key = entry.Substring(0, separator).Trim();
                string value = entry.Substring(separator + 1).Trim();
                if (key.Length == 0 || !float.TryParse(value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out float mapChance))
                {
                    continue;
                }

                parsed[key] = mapChance;
            }

            return parsed;
        }

        // private void HandleHotkey(ArtilleryShellingControllerServer controller)
        // {
        //     Player player = Singleton<GameWorld>.Instance?.MainPlayer;
        //     if (player == null || !player.HealthController.IsAlive)
        //     {
        //         return;
        //     }
        //
        //     Vector3 target = player.Position;
        //     if (HotkeyTarget.Value == EHotkeyTarget.Crosshair && TryGetCrosshairPoint(out Vector3 crosshairPoint))
        //     {
        //         target = crosshairPoint;
        //     }
        //
        //     StrikeService.TryCallStrike(target, "hotkey", player);
        // }

        // internal static bool TryGetCrosshairPoint(out Vector3 point)
        // {
        //     point = default;
        //
        //     Camera camera = Camera.main;
        //     if (camera == null)
        //     {
        //         return false;
        //     }
        //
        //     Ray ray = camera.ScreenPointToRay(Input.mousePosition);
        //     if (Physics.Raycast(ray, out RaycastHit hit, CrosshairMaxDistance.Value,
        //             EFTHardSettings.Instance.MOVEMENT_MASK))
        //     {
        //         point = hit.point;
        //         return true;
        //     }
        //
        //     return false;
        // }
    }
}