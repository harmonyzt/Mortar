using System.Collections.Generic;
using System.Reflection;
using CommonAssets.Scripts.ArtilleryShelling.Client;
using EFT;
using EFT.Communications;
using EFT.GlobalEvents;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Mortar.Patches
{
    /// <summary>
    /// SPT locale data has no "ShellingWarningMessage" key, so we gotta have custom thing
    /// </summary>
    internal class ShellingWarningPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(ArtilleryAlarmLogic), nameof(ArtilleryAlarmLogic.ShowNotifyMessage));
        }

        [PatchPrefix]
        private static bool Prefix()
        {
            if (!CoopSupport.Enabled)
            {
                return true;
            }

            EyeWarning.Queue();

            // solo fallback
            if (!CoopSupport.IsClient)
            {
                MapSiren.Trigger();
            }

            // Suppress both the custom and the vanilla notification
            if (!Plugin.EnableWarningText.Value)
            {
                return false;
            }

            if (string.IsNullOrEmpty(Plugin.CustomWarningText.Value))
            {
                return true;
            }

            NotificationManager.DisplayMessageNotification(Plugin.CustomWarningText.Value);

            return false;
        }
    }

    /// <summary>
    /// Show the configured text instead of the raw key. (because SPT doesn't have a locale key for it)
    /// </summary>
    internal class ShellingBusyPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(ArtilleryAlarmLogic), nameof(ArtilleryAlarmLogic.OnNotifyEvent));
        }

        [PatchPrefix]
        private static bool Prefix(NotifyEvent notifyEvent)
        {
            if (!Plugin.Enable.Value || notifyEvent == null)
            {
                return true;
            }

            if (notifyEvent.MessageType != NotifyEvent.EMessageType.ArtilleryBusy)
            {
                return true;
            }

            if (GamePlayerOwner.MyPlayer == null)
            {
                return false;
            }

            if (!notifyEvent.ReceiverId.Equals(GamePlayerOwner.MyPlayer.PlayerId))
            {
                return true;
            }

            if (string.IsNullOrEmpty(Plugin.CustomBusyText.Value))
            {
                return true;
            }

            NotificationManager.DisplayMessageNotification(Plugin.CustomBusyText.Value);

            return false;
        }
    }

    /// <summary>
    /// This notifies players carrying the artillery detector
    /// </summary>
    internal class ShellingNotifyEveryonePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(ArtilleryAlarmLogic),
                nameof(ArtilleryAlarmLogic.ReceiveShellingNotificationToPlayers));
        }

        [PatchPrefix]
        private static bool Prefix(ref int[] playersToNotify)
        {
            Player myPlayer = GamePlayerOwner.MyPlayer;
            if (myPlayer == null)
            {
                return false;
            }

            if (!CoopSupport.Enabled || !CoopSupport.WarnEveryone)
            {
                return true;
            }

            int myId = myPlayer.PlayerId;

            if (playersToNotify != null)
            {
                for (int i = 0; i < playersToNotify.Length; i++)
                {
                    if (playersToNotify[i] == myId)
                    {
                        return true;
                    }
                }
            }

            List<int> receivers = playersToNotify != null ? new List<int>(playersToNotify) : new List<int>();
            receivers.Add(myId);
            playersToNotify = receivers.ToArray();

            return true;
        }
    }

    /// <summary>
    /// CheckPlayersAlertState() only feeds alarm stages / beeps to players holding item 6655e35b6bc645cb7b059912.
    /// This makes the local player count as carrying it.
    /// </summary>
    internal class ForceDetectorPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(ArtilleryShellingControllerServer),
                nameof(ArtilleryShellingControllerServer.HasDetector));
        }

        [PatchPrefix]
        private static bool Prefix(Player player, ref bool __result)
        {
            if (!CoopSupport.Enabled || !CoopSupport.ForceDetector || player == null)
            {
                return true;
            }

            Player myPlayer = GamePlayerOwner.MyPlayer;
            if (myPlayer != null && myPlayer.PlayerId == player.PlayerId)
            {
                __result = true;

                return false;
            }

            return true;
        }
    }
}