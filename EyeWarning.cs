using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace Mortar
{
    public static class EyeWarning
    {
        public const string TemplateId = "6655e35b6bc645cb7b059912";
        private const float StowTime = 0.6f;
        private static readonly int UseState = Animator.StringToHash("Hands.USE");
        public static event Action<bool> HoldChanged;
        private static readonly Dictionary<string, bool> RemoteHolds = new Dictionary<string, bool>();

        private static readonly Dictionary<string, Player.QuickUseItemController> HeldControllers =
            new Dictionary<string, Player.QuickUseItemController>();

        private static readonly HashSet<string> MutedPlayers = new HashSet<string>();
        private static readonly HashSet<BetterSource> AlarmSources = new HashSet<BetterSource>();

        private static readonly Dictionary<string, BaseSoundPlayer> RemoteSwitchSounds =
            new Dictionary<string, BaseSoundPlayer>();

        private static readonly HashSet<Player.QuickUseItemController> StowingControllers =
            new HashSet<Player.QuickUseItemController>();

        private static Player _player;
        private static Player.QuickUseItemController _controller;
        private static bool _pending;
        private static float _drawAt = float.PositiveInfinity;
        private static bool _drawing;
        private static float _speed;
        private static BaseSoundPlayer _switchSound;

        internal static bool IsEye(Item item) => item != null && (string)item.TemplateId == TemplateId;
        public static bool IsMuted(IPlayer player) => player != null && MutedPlayers.Contains(player.ProfileId);

        internal static bool ControlsLeftArm(Player player)
        {
            if (player != _player || !(player.HandsController is Player.QuickUseItemController hands) ||
                hands.Destroyed)
            {
                return false;
            }

            return IsEye(((IHandsController)hands).Item)
                   && (_drawing || hands == _controller || StowingControllers.Contains(hands));
        }

        internal static void TrackAlarmSource(IPlayer player, BetterSource source)
        {
            if (!CoopSupport.Enabled || !Plugin.EnableEyeInteraction.Value || player == null
                || !player.IsYourPlayer || source == null)
            {
                return;
            }

            if (AlarmSources.Add(source))
            {
                source.OnReleased += AlarmReleased;
            }

            if (!_drawing && _controller == null && (!_pending || float.IsPositiveInfinity(_drawAt)))
            {
                _pending = true;
                _drawAt = Time.time + UnityEngine.Random.Range(5f, 10f);
            }
        }

        private static void AlarmReleased(BetterSource source)
        {
            source.OnReleased -= AlarmReleased;

            AlarmSources.Remove(source);
        }

        private static void StopAlarm()
        {
            foreach (BetterSource source in AlarmSources.ToArray())
            {
                AlarmReleased(source);
                if (source != null)
                {
                    source.Stop(0f);
                }
            }
        }

        internal static void Queue()
        {
            if (!CoopSupport.Enabled || !Plugin.EnableEyeInteraction.Value)
            {
                return;
            }

            Player player = Singleton<GameWorld>.Instance?.MainPlayer;
            if (player == null || !player.HealthController.IsAlive)
            {
                return;
            }

            MutedPlayers.Remove(player.ProfileId);
            if (!_pending && !_drawing && _controller == null)
            {
                _pending = true;
                _drawAt = float.PositiveInfinity;
            }
        }

        public static void SetRemoteHold(string profileId, bool holding)
        {
            if (string.IsNullOrEmpty(profileId))
            {
                return;
            }

            RemoteHolds[profileId] = holding;
            if (holding)
            {
                MutedPlayers.Remove(profileId);
            }
            else
            {
                MutedPlayers.Add(profileId);
            }

            if (!holding && HeldControllers.TryGetValue(profileId, out Player.QuickUseItemController controller))
            {
                if (!controller.Destroyed)
                {
                    controller.FirearmsAnimator.SetAnimationSpeed(1f);
                    StowingControllers.Add(controller);
                }

                HeldControllers.Remove(profileId);
                if (RemoteSwitchSounds.TryGetValue(profileId, out BaseSoundPlayer sound) && sound != null)
                {
                    sound.SoundEventHandler("Switch");
                }

                RemoteSwitchSounds.Remove(profileId);
            }
        }

        internal static bool OnSwitchSound(BaseSoundPlayer sound, FirearmsAnimator animator)
        {
            GameWorld world = Singleton<GameWorld>.Instance;
            Player player =
                world?.AllAlivePlayersList.FirstOrDefault(p => p.HandsController?.FirearmsAnimator == animator);
            if (!(player?.HandsController is Player.QuickUseItemController controller))
            {
                return true;
            }

            // enable for debug
            //EyeAnimationProbe.OnSwitch(controller);
            if (!IsEye(((IHandsController)controller).Item))
            {
                return true;
            }

            if (player.IsYourPlayer)
            {
                if (!_drawing || player != _player)
                {
                    return true;
                }

                // enable for debug
                // if (EyeAnimationProbe.Unpaused)
                // {
                //     _drawing = false;
                //     HoldChanged?.Invoke(false);
                //     return true;
                // }

                _controller = controller;
                _drawing = false;
                _switchSound = sound;
                _speed = controller.FirearmsAnimator.Animator.speed;
                controller.FirearmsAnimator.SetAnimationSpeed(0f);
                return false;
            }
            else if (RemoteHolds.TryGetValue(player.ProfileId, out bool holding) && holding)
            {
                HeldControllers[player.ProfileId] = controller;
                RemoteSwitchSounds[player.ProfileId] = sound;
                controller.FirearmsAnimator.SetAnimationSpeed(0f);
                return false;
            }

            return true;
        }

        internal static void Update()
        {
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null || world.ClientShellingController == null || !CoopSupport.Enabled)
            {
                Reset();

                return;
            }

            // enable for debug
            // EyeAnimationProbe.Update();
            UpdateStowing();

            Player player = world.MainPlayer;
            if (player == null || !player.HealthController.IsAlive || !Plugin.EnableEyeInteraction.Value)
            {
                ResetLocal();
                return;
            }

            if (_player != null && player != _player)
            {
                ResetLocal();
            }

            if (_controller != null)
            {
                if (_controller.Destroyed || player.HandsController != _controller)
                {
                    Release();
                    return;
                }

                if (!player.IsInventoryOpened && Input.GetMouseButtonDown(0))
                {
                    MutedPlayers.Add(player.ProfileId);
                    StopAlarm();
                    Release(true);
                }

                return;
            }

            if (!_pending || Time.time < _drawAt || _drawing || player.IsInventoryOpened || player.IsSprintEnabled
                || player.ProcessStatus != Player.EProcessStatus.None || player.HandsController == null)
            {
                return;
            }

            IHandsController hands = player.HandsController;
            if (!hands.CanRemove() || hands.IsHandsProcessing() || hands.IsInInteractionStrictCheck()
                || hands.InCanNotBeInterruptedOperation())
            {
                return;
            }

            Item eye = player.Inventory.GetPlayerItems(EPlayerItems.NonQuestItems).FirstOrDefault(IsEye);
            if (eye == null)
            {
                _pending = false;
                return;
            }

            _player = player;
            _pending = false;
            _drawing = true;
            try
            {
                HoldChanged?.Invoke(true);
                // Proceed owns the hands transition and Fika's normal inventory/hands replication.
                player.Proceed(eye, (Result<IQuickUseItem> result) =>
                {
                    if (result.Failed)
                    {
                        _drawing = false;
                        HoldChanged?.Invoke(false);
                        Plugin.LogSource.LogWarning($"[Mortair] Eye hands transition failed: {result.Error}");
                    }
                }, false);
            }
            catch (Exception ex)
            {
                _drawing = false;
                HoldChanged?.Invoke(false);
                Plugin.LogSource.LogError($"[Mortair] Eye hands transition failed: {ex}");
            }
        }

        internal static void OnUseCompleted(Player.QuickUseItemController controller)
        {
            // enable for debug
            // EyeAnimationProbe.Complete(controller);
            StowingControllers.Remove(controller);
            if (!_drawing || controller._player != _player || !IsEye(((IHandsController)controller).Item))
            {
                return;
            }

            _drawing = false;
            HoldChanged?.Invoke(false);
            Plugin.LogSource.LogWarning("[Mortair] Eye animation finished without reaching the Switch hold event.");
        }

        private static void Release(bool switchOff = false)
        {
            if (_controller != null && !_controller.Destroyed)
            {
                _controller.FirearmsAnimator.SetAnimationSpeed(_speed);
                if (switchOff)
                {
                    StowingControllers.Add(_controller);
                }
            }

            bool wasHolding = _controller != null || _drawing;
            _controller = null;
            _drawing = false;
            if (switchOff && _switchSound != null)
            {
                _switchSound.SoundEventHandler("Switch");
            }

            _switchSound = null;
            if (wasHolding)
            {
                HoldChanged?.Invoke(false);
            }
        }

        private static void UpdateStowing()
        {
            foreach (Player.QuickUseItemController controller in StowingControllers.ToArray())
            {
                if (controller == null || controller.Destroyed || controller._player.HandsController != controller)
                {
                    StowingControllers.Remove(controller);
                    continue;
                }

                IAnimator animator = controller.FirearmsAnimator.Animator;
                int layer = animator.GetLayerIndex("Hands");
                if (layer < 0)
                {
                    StowingControllers.Remove(controller);
                    continue;
                }

                AnimatorStateInfoWrapper state = animator.GetCurrentAnimatorStateInfo(layer);
                if (state.fullPathHash == UseState && state.normalizedTime < StowTime)
                {
                    continue;
                }

                StowingControllers.Remove(controller);
                if (state.fullPathHash != UseState)
                {
                    continue;
                }

                // Skip the placement anim and cleanup
                animator.Play("Hands.Spawn", layer, 0f);
                controller.IEventsConsumerOnThirdAction(2);
                controller.OnUseCompleteAction();
            }
        }

        private static void ResetLocal()
        {
            Release();
            StopAlarm();
            if (_player != null)
            {
                MutedPlayers.Remove(_player.ProfileId);
            }

            _player = null;
            _pending = false;
            _drawAt = float.PositiveInfinity;
            _drawing = false;
        }

        internal static void Reset()
        {
            // enable for debug
            //EyeAnimationProbe.Reset();
            ResetLocal();
            foreach (Player.QuickUseItemController controller in HeldControllers.Values)
            {
                if (!controller.Destroyed)
                {
                    controller.FirearmsAnimator.SetAnimationSpeed(1f);
                }
            }

            HeldControllers.Clear();
            RemoteSwitchSounds.Clear();
            RemoteHolds.Clear();
            MutedPlayers.Clear();
            StowingControllers.Clear();
        }
    }
}