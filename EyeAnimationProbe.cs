// using EFT;
// using UnityEngine;
//
// namespace Mortair
// {
//     public enum EyeProbeMode
//     {
//         Off,
//         Unpaused,
//         Paused
//     }
//
//     internal static class EyeAnimationProbe
//     {
//         private const string JammerTemplateId = "5ac78a9b86f7741cca0bbd8d";
//         private static readonly float[] SampleTimes = { 0.5f, 1.1f, 2.2f, 4f };
//         private static Player.QuickUseItemController _controller;
//         private static EyeProbeMode _mode;
//         private static float _startedAt;
//         private static int _sample;
//
//         internal static bool Enabled => Plugin.EyeProbe.Value != EyeProbeMode.Off;
//         internal static bool Unpaused => Plugin.EyeProbe.Value == EyeProbeMode.Unpaused;
//
//         internal static void Begin(Player.QuickUseItemController controller)
//         {
//             if (!Enabled || !controller._player.IsYourPlayer)
//             {
//                 return;
//             }
//
//             string template = (string)((IHandsController)controller).Item.TemplateId;
//             if (template != EyeWarning.TemplateId && template != JammerTemplateId)
//             {
//                 return;
//             }
//
//             _controller = controller;
//             _mode = Plugin.EyeProbe.Value;
//             _startedAt = Time.time;
//             _sample = 0;
//             Capture("spawn");
//         }
//
//         internal static void OnSwitch(Player.QuickUseItemController controller)
//         {
//             if (_controller != null && controller == _controller)
//             {
//                 Capture("switch");
//             }
//         }
//
//         internal static void Complete(Player.QuickUseItemController controller)
//         {
//             if (_controller == null || controller != _controller)
//                 return;
//             Capture("use-complete");
//             Reset();
//         }
//
//         internal static void Update()
//         {
//             if (_controller == null)
//             {
//                 return;
//             }
//             if (!Enabled || Plugin.EyeProbe.Value != _mode || _controller.Destroyed
//                 || _controller._player.HandsController != _controller)
//             {
//                 Reset();
//                 return;
//             }
//
//             if (_sample < SampleTimes.Length && Time.time - _startedAt >= SampleTimes[_sample])
//             {
//                 _sample++;
//                 Capture("sample");
//             }
//         }
//
//         internal static void Reset() => _controller = null;
//
//         private static void Capture(string phase)
//         {
//             Player player = _controller._player;
//             IAnimator body = player.BodyAnimatorCommon;
//             IAnimator hands = _controller.FirearmsAnimator.Animator;
//             float curve = player.GetCurveValue(PlayerAnimator.FIRST_PERSON_CURVE_WEIGHT);
//             float left = player.GetCurveValue(PlayerAnimator.LEFT_HAND_WEIGHT);
//             float shoulder = player.GetCurveValue(PlayerAnimator.LEFT_SHOULDER_WEIGHT);
//             Transform marker = _controller.HandsHierarchy.GetTransform(ECharacterWeaponBones.weapon_L_IK_marker);
//             Plugin.LogSource.LogInfo(
//                 $"[Mortair EyeProbe] mode={_mode}; phase={phase}; elapsed={Time.time - _startedAt:F3}; "
//                 + $"item={((IHandsController)_controller).Item.TemplateId}; player={player.GetType().Name}; "
//                 + $"hands={State(hands, "Hands")}; speed={hands.speed:F3}; "
//                 + $"bodyWeapon={State(body, "Weapon")}; bodySpeed={body.speed:F3}; firstAction={body.GetInteger(PlayerAnimator.FIRST_PERSON_ACTION)}; "
//                 + $"fpCurve={curve:F3}; leftCurve={left:F3}; shoulderCurve={shoulder:F3}; nativeLeftIK={1f - left * curve:F3}; "
//                 + $"leftTarget={Position(player, marker)}; leftPalm={Position(player, player.PlayerBones.LeftPalm)}; "
//                 + $"leftUpperArm={Position(player, player.PlayerBones.Shoulders[0])}");
//         }
//
//         private static string State(IAnimator animator, string name)
//         {
//             int layer = animator.GetLayerIndex(name);
//             if (layer < 0)
//             {
//                 return "missing";
//             }
//
//             AnimatorStateInfoWrapper state = animator.GetCurrentAnimatorStateInfo(layer);
//
//             return $"{state.fullPathHash:X8}@{state.normalizedTime:F3}";
//         }
//
//         private static string Position(Player player, Transform bone)
//         {
//             if (bone == null)
//             {
//                 return "missing";
//             }
//
//             return (Quaternion.Inverse(player.Transform.rotation)
//                     * (bone.position - player.PlayerBones.Spine3.position)).ToString("F3");
//         }
//     }
// }