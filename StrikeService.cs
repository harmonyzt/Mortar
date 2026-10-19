using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommonAssets.Scripts.ArtilleryShelling;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.GlobalEvents;
using Mortar;
using UnityEngine;

namespace Mortair
{
    /// <summary>
    /// Place where a real mortar strike is started
    /// </summary>
    public static class StrikeService
    {
        private static readonly HashSet<string> SynthesizedMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static float _lastStrikeTime = float.NegativeInfinity;
        private static readonly HashSet<Task> PendingStrikes = new HashSet<Task>();

        internal static void TrackPending(Task task)
        {
            if (task != null && !task.IsCompleted)
                PendingStrikes.Add(task);
        }

        internal static void MarkSynthesized(string locationId)
        {
            if (!string.IsNullOrEmpty(locationId))
            {
                SynthesizedMaps.Add(locationId);
            }
        }

        internal static bool IsSynthesized(string locationId)
        {
            return !string.IsNullOrEmpty(locationId) && SynthesizedMaps.Contains(locationId);
        }

        internal static void ClearSynthesizedMaps() => SynthesizedMaps.Clear();

        internal static void ResetRaidState()
        {
            _lastStrikeTime = float.NegativeInfinity;
            PendingStrikes.Clear();
        }

        public static bool IsBusy(ArtilleryShellingControllerServer controller)
        {
            if (controller == null)
            {
                return true;
            }

            int max = controller._artilleryShellingGlobalsSettings != null
                ? controller._artilleryShellingGlobalsSettings.MaxCalledShellingCount
                : 3;
            if (max < 1)
            {
                max = 1;
            }

            PendingStrikes.RemoveWhere(task => task.IsCompleted);
            return controller.dictionary_0.Count + controller._zonesInEventShelling.Count + PendingStrikes.Count >= max;
        }

        public static bool CanStrikeNow(ArtilleryShellingControllerServer controller)
        {
            if (controller == null || !Plugin.Enable.Value)
            {
                return false;
            }

            if (Time.realtimeSinceStartup - _lastStrikeTime < Plugin.StrikeCooldownSeconds.Value)
            {
                return false;
            }

            return !IsBusy(controller);
        }

        public static bool TryCallStrike(Vector3 position, string reason, IPlayer source = null)
        {
            if (!CoopSupport.Enabled)
            {
                return false;
            }

            if (!IsFinite(position.x) || !IsFinite(position.y) || !IsFinite(position.z))
            {
                return false;
            }

            if (CoopSupport.IsClient)
            {
                return CoopSupport.Network.RequestStrike(position, reason, source);
            }

            ArtilleryShellingControllerServer controller = ArtilleryShellingControllerServer.Instance;
            if (controller == null)
            {
                Plugin.LogSource.LogDebug($"[Mortair] strike ({reason}) ignored: no shelling controller on this map.");
                return false;
            }

            if (Time.realtimeSinceStartup - _lastStrikeTime < Plugin.StrikeCooldownSeconds.Value)
            {
                Plugin.LogSource.LogDebug($"[Mortair] strike ({reason}) ignored: cooldown.");
                return false;
            }

            if (IsBusy(controller))
            {
                Plugin.LogSource.LogDebug($"[Mortair] strike ({reason}) rejected: MaxCalledShellingCount reached.");
                NotifyBusy(source);
                return false;
            }

            if (!ValidateMapConfig(controller, out string problem))
            {
                Plugin.LogSource.LogWarning($"[Mortair] strike ({reason}) rejected: {problem}");
                return false;
            }

            if (IsSynthesized(controller._gameWorld != null ? controller._gameWorld.LocationId : null))
            {
                RepositionGuns(controller, position);
            }

            controller.StartShellingPosition(position);
            _lastStrikeTime = Time.realtimeSinceStartup;
            
            //Plugin.LogSource.LogInfo($"[Mortair] strike ({reason}) called at {position}.");
            
            return true;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool ValidateMapConfig(ArtilleryShellingControllerServer controller, out string problem)
        {
            problem = null;

            ArtilleryShellingZone[] zones = controller._currentMapConfiguration.ShellingZones;
            if (zones == null || zones.Length == 0)
            {
                problem = "map config has no shelling zones.";
                
                return false;
            }

            bool hasCallableZone = false;
            for (int i = 0; i < zones.Length; i++)
            {
                if (zones[i].UseInCalledShelling)
                {
                    hasCallableZone = true;
                    break;
                }
            }

            if (!hasCallableZone)
            {
                problem = "map config has no zone with UseInCalledShelling.";
                
                return false;
            }

            ArtilleryBrigade[] brigades = controller._currentMapConfiguration.Brigades;
            if (brigades == null || brigades.Length == 0)
            {
                problem = "map config has no brigades.";
                
                return false;
            }

            for (int i = 0; i < brigades.Length; i++)
            {
                ArtilleryGun[] guns = brigades[i].ArtilleryGuns;
                if (guns == null || guns.Length < 2)
                {
                    problem = $"brigade {brigades[i].ID} has fewer than 2 guns.";
                    
                    return false;
                }
            }

            return true;
        }

        private static void RepositionGuns(ArtilleryShellingControllerServer controller, Vector3 target)
        {
            ArtilleryBrigade[] brigades = controller._currentMapConfiguration.Brigades;
            if (brigades == null)
            {
                return;
            }

            // _currentMapConfiguration is a struct copy, but Brigades is a reference to the same array
            // and ArtilleryGun is a class, so these mutations are visible to Shot().
            float distance = Plugin.SynthGunDistance.Value;
            float angle = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
            Vector3 battery = target + new Vector3(Mathf.Cos(angle) * distance, 0f, Mathf.Sin(angle) * distance);

            if (Physics.Raycast(battery + Vector3.up * 300f, Vector3.down, out RaycastHit hit, 400f, EFTHardSettings.Instance.MOVEMENT_MASK))
            {
                battery = hit.point;
            }
            else
            {
                battery.y = target.y;
            }

            for (int i = 0; i < brigades.Length; i++)
            {
                ArtilleryGun[] guns = brigades[i].ArtilleryGuns;
                if (guns == null)
                {
                    continue;
                }

                for (int j = 0; j < guns.Length; j++)
                {
                    if (guns[j] != null)
                    {
                        guns[j].Position = battery;
                    }
                }
            }
        }

        private static void NotifyBusy(IPlayer source)
        {
            try
            {
                if (source != null)
                {
                    NotifyEvent notifyEvent = GlobalEventsController.CreateEvent<NotifyEvent>();
                    notifyEvent.MessageType = NotifyEvent.EMessageType.ArtilleryBusy;
                    notifyEvent.ReceiverId = source.Id;
                    notifyEvent.Invoke();
                    return;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogDebug($"[Mortair] busy NotifyEvent failed: {ex.Message}");
            }

            if (!string.IsNullOrEmpty(Plugin.CustomBusyText.Value))
            {
                NotificationManager.DisplayMessageNotification(Plugin.CustomBusyText.Value);
            }
        }
    }
}
