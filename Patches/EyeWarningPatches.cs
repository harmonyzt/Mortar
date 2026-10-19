using System.Reflection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using CommonAssets.Scripts.ArtilleryShelling.Client;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Mortair;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Mortar.Patches
{
    internal class EyeGripPatch : ModulePatch
    {
        private sealed class Grip
        {
            internal Transform Original;
            internal Transform Offset;
        }

        private static readonly ConditionalWeakTable<WeaponPrefab, Grip> Grips =
            new ConditionalWeakTable<WeaponPrefab, Grip>();

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(ObjectsFactory), "CreateItemUsablePrefab",
                new[] { typeof(Item), typeof(IPlayer) });

        [PatchPostfix]
        private static void Postfix(Item __0, GameObject __result)
        {
            if (!Plugin.Enable.Value || !EyeWarning.IsEye(__0) || __result == null)
            {
                return;
            }

            var hands = __result.GetComponent<UsableHandsPrefab>();
            if (hands == null || hands.ItemSpawnTransform == null)
            {
                Plugin.LogSource.LogWarning("[Mortar] Eye grip attachment was not found.");
                return;
            }

            var offset = new GameObject("Mortar_EyeGrip").transform;
            offset.SetParent(hands.ItemSpawnTransform, false);
            
            // Match the Eye's Y-axis case and base pivot to the jammer's Z-axis case center
            offset.localRotation = Quaternion.Euler(90f, 0f, 0f);
            offset.localPosition = new Vector3(0f, 0.0002f, -0.102f);
            Grips.Add(hands, new Grip { Original = hands.ItemSpawnTransform, Offset = offset });
            hands.ItemSpawnTransform = offset;
        }

        internal static void Restore(WeaponPrefab prefab)
        {
            if (!Grips.TryGetValue(prefab, out Grip grip))
            {
                return;
            }

            Grips.Remove(prefab);

            ((UsableHandsPrefab)prefab).ItemSpawnTransform = grip.Original;

            if (grip.Offset != null)
            {
                UnityEngine.Object.Destroy(grip.Offset.gameObject);
            }
        }
    }

    internal class EyeGripResetPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(WeaponPrefab), "ReturnToPool");

        // Restore after native cleanup has returned the attached item to its pool
        [PatchPostfix]
        private static void Postfix(WeaponPrefab __instance) => EyeGripPatch.Restore(__instance);
    }

    internal class EyeLeftArmBlendPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(Player), "VisualPass");

        [PatchTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var handWeight = AccessTools.Field(typeof(Player), "_firstPersonLeftHand");
            var shoulderCurve = AccessTools.Field(typeof(PlayerAnimator), "LEFT_SHOULDER_WEIGHT");
            var getCurve = AccessTools.Method(typeof(Player), "GetCurveValue");

            int hand = code.FindIndex(instruction =>
                instruction.opcode == OpCodes.Stfld && Equals(instruction.operand, handWeight));

            int shoulder = code.FindIndex(instruction =>
                instruction.opcode == OpCodes.Ldsfld && Equals(instruction.operand, shoulderCurve));

            if (hand < 0 || shoulder < 0 || shoulder + 1 >= code.Count || !code[shoulder + 1].Calls(getCurve) ||
                code.Count(instruction =>
                    instruction.opcode == OpCodes.Stfld && Equals(instruction.operand, handWeight)) != 1 ||
                code.Count(instruction =>
                    instruction.opcode == OpCodes.Ldsfld && Equals(instruction.operand, shoulderCurve)) != 1)
            {
                throw new InvalidOperationException("Mortar: left-arm blend layout changed.");
            }

            code.InsertRange(hand, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(EyeLeftArmBlendPatch), nameof(HandWeight)))
            });

            shoulder = code.FindIndex(instruction =>
                instruction.opcode == OpCodes.Ldsfld && Equals(instruction.operand, shoulderCurve));

            code.InsertRange(shoulder + 2, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(EyeLeftArmBlendPatch), nameof(ShoulderCurve)))
            });

            return code;
        }

        // Zero hand IK and a full shoulder curve leave the left arm on its body pose.
        private static bool UseBodyPose(Player player) => player.FirstPersonPointOfView &&
                                                          EyeWarning.ControlsLeftArm(player);

        private static float HandWeight(float native, Player player) => UseBodyPose(player) ? 0f : native;
        private static float ShoulderCurve(float native, Player player) => UseBodyPose(player) ? 1f : native;

        [PatchPostfix]
        private static void Postfix(Player __instance)
        {
            if (!UseBodyPose(__instance) || !__instance.HealthController.IsAlive)
            {
                return;
            }

            var bones = __instance.PlayerBones;
            Transform upper = bones.Shoulders[0];
            Transform forearm = bones.Forearms[0];
            Transform hand = bones.LeftPalm;

            if (upper == null || forearm == null || hand == null)
            {
                return;
            }

            Vector3 down = (bones.Pelvis.position - bones.Spine3.position).normalized;
            Vector3 outward = -__instance.Transform.right;

            // Cover the draw too; blending from the native pose exposes its raised left hand
            upper.rotation = Quaternion.FromToRotation(forearm.position - upper.position, down + outward * 0.08f) *
                             upper.rotation;

            forearm.rotation =
                Quaternion.FromToRotation(hand.position - forearm.position,
                    down + __instance.Transform.forward * 0.12f) * forearm.rotation;
        }
    }

    internal class EyeSwitchSoundPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BaseSoundPlayer), "SoundEventHandler");

        [PatchPrefix]
        private static bool Prefix(BaseSoundPlayer __instance, string __0, FirearmsAnimator ____firearmsAnimator)
        {
            if (!CoopSupport.Enabled)
            {
                return true;
            }

            switch (__0)
            {
                case "Switch":
                {
                    return EyeWarning.OnSwitchSound(__instance, ____firearmsAnimator);
                }
                case "Place":
                {
                    var world = Comfort.Common.Singleton<GameWorld>.Instance;
                    if (world != null)
                    {
                        foreach (Player player in world.AllAlivePlayersList)
                            if (player.HandsController?.FirearmsAnimator == ____firearmsAnimator
                                && EyeWarning.IsEye(((IHandsController)player.HandsController).Item))
                            {
                                return false;
                            }
                    }

                    break;
                }
            }

            return true;
        }
    }

    internal class EyeUseCompletePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player.QuickUseItemController), "OnUseCompleteAction");

        [PatchPrefix]
        private static void Prefix(Player.QuickUseItemController __instance) => EyeWarning.OnUseCompleted(__instance);
    }

    // Debugging here, dont enable
    // internal class EyeProbeSpawnPatch : ModulePatch
    // {
    //     protected override MethodBase GetTargetMethod() =>
    //         AccessTools.Method(typeof(Player.QuickUseItemController), "Spawn");
    //
    //     [PatchPostfix]
    //     private static void Postfix(Player.QuickUseItemController __instance) => EyeAnimationProbe.Begin(__instance);
    // }

    internal class EyeAlarmPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(ArtilleryAlarmLogic), "InvokeAlarmSignal");

        [PatchPrefix]
        private static bool Prefix(IPlayer player) => !CoopSupport.Enabled || !EyeWarning.IsMuted(player);

        [PatchTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var enableStereo = AccessTools.Method(typeof(BetterSource), "EnableStereo");
            int index = code.FindIndex(instruction => instruction.Calls(enableStereo));
            if (index < 2 || code[index - 2].opcode != OpCodes.Ldloc_3
                          || code.Count(instruction => instruction.Calls(enableStereo)) != 1)
            {
                throw new InvalidOperationException("Mortar: artillery alarm source layout changed.");
            }

            code.InsertRange(index + 1, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Ldloc_3),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(EyeWarning), "TrackAlarmSource"))
            });

            return code;
        }
    }
}