using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEditor.Animations;

namespace Game.Editor
{
    // 用途: 在Project面板选中AnimatorController资源时, 通过右键菜单生成/更新"Clip-Only"控制器。
    public static class CreateClipOnlyAnimatorControllerTool
    {
        private const string MenuPath = "Assets/动画工具/生成Clip-Only控制器";

        [MenuItem(MenuPath, true)]
        private static bool ValidateGenerate()
        {
            return Selection.objects != null && Selection.objects.Any(o => o is AnimatorController);
        }

        [MenuItem(MenuPath)]
        private static void Generate()
        {
            var selectedControllers = Selection.objects
                .OfType<AnimatorController>()
                .Distinct()
                .ToArray();

            if (selectedControllers.Length == 0)
            {
                Debug.LogWarning("[ClipOnly] 未选中AnimatorController资源.");
                return;
            }

            try
            {
                AssetDatabase.StartAssetEditing();

                for (int i = 0; i < selectedControllers.Length; i++)
                {
                    var src = selectedControllers[i];
                    EditorUtility.DisplayProgressBar("生成Clip-Only控制器", $"处理 {src.name} ({i + 1}/{selectedControllers.Length})", (float)i / selectedControllers.Length);

                    try
                    {
                        ProcessOne(src);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[ClipOnly] 处理 {src.name} 失败: {e}");
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                EditorUtility.ClearProgressBar();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
        }

        private static void ProcessOne(AnimatorController srcController)
        {
            var srcPath = AssetDatabase.GetAssetPath(srcController);
            if (string.IsNullOrEmpty(srcPath))
            {
                Debug.LogError($"[ClipOnly] 源控制器未找到有效路径: {srcController.name}");
                return;
            }

            var dir = Path.GetDirectoryName(srcPath).Replace("\\", "/");
            var fileNameNoExt = Path.GetFileNameWithoutExtension(srcPath);
            var destPath = $"{dir}/{fileNameNoExt}_ClipOnly_DoNotEditDirectly.controller";

            var exists = File.Exists(destPath);
            AnimatorController destController;

            if (exists)
            {
                destController = AssetDatabase.LoadAssetAtPath<AnimatorController>(destPath);
                if (destController == null)
                {
                    Debug.LogError($"[ClipOnly] 目标文件已存在但无法加载为AnimatorController: {destPath}");
                    return;
                }

                Debug.Log($"[ClipOnly] 更新已存在的控制器(保持引用不变): {destPath}");
                ClearAllLayers(destController);
            }
            else
            {
                Debug.Log($"[ClipOnly] 创建新的控制器: {destPath}");
                destController = AnimatorController.CreateAnimatorControllerAtPath(destPath);
                ClearAllLayers(destController);
            }

            CopyParameters(srcController, destController);
            BuildClipOnly(srcController, destController);

            EditorUtility.SetDirty(destController);
            Debug.Log($"[ClipOnly] 完成: {srcController.name} -> {destController.name} ({destPath})");
        }

        private static void ClearAllLayers(AnimatorController controller)
        {
            var cnt = controller.layers != null ? controller.layers.Length : 0;
            for (int i = cnt - 1; i >= 0; i--)
                controller.RemoveLayer(i);
        }

        private static void CopyParameters(AnimatorController src, AnimatorController dest)
        {
            var destParams = dest.parameters ?? Array.Empty<AnimatorControllerParameter>();
            for (int i = destParams.Length - 1; i >= 0; i--)
                dest.RemoveParameter(destParams[i]);

            var srcParams = src.parameters ?? Array.Empty<AnimatorControllerParameter>();
            foreach (var sp in srcParams)
            {
                var np = new AnimatorControllerParameter
                {
                    name = sp.name,
                    type = sp.type,
                    defaultBool = sp.defaultBool,
                    defaultFloat = sp.defaultFloat,
                    defaultInt = sp.defaultInt
                };
                dest.AddParameter(np);
                Debug.Log($"[ClipOnly] 参数复制: {np.name} ({np.type})");
            }
            Debug.Log($"[ClipOnly] 参数同步完成: 共 {srcParams.Length} 个参数.");
        }

        private static void BuildClipOnly(AnimatorController src, AnimatorController dest)
        {
            var srcLayers = src.layers ?? Array.Empty<AnimatorControllerLayer>();

            for (int li = 0; li < srcLayers.Length; li++)
            {
                var srcLayer = srcLayers[li];

                var newLayer = new AnimatorControllerLayer
                {
                    name = srcLayer.name,
                    avatarMask = srcLayer.avatarMask,
                    blendingMode = srcLayer.blendingMode,
                    defaultWeight = srcLayer.defaultWeight,
                    iKPass = srcLayer.iKPass,
                    syncedLayerAffectsTiming = false,
                    syncedLayerIndex = -1,
                    stateMachine = new AnimatorStateMachine
                    {
                        name = $"{srcLayer.stateMachine?.name ?? srcLayer.name}_ClipOnly"
                    }
                };

                try { AssetDatabase.AddObjectToAsset(newLayer.stateMachine, dest); } catch { }
                dest.AddLayer(newLayer);

                var sm = newLayer.stateMachine;

                // 复制“根状态机”上的 StateMachineBehaviour
                if (srcLayer.stateMachine != null)
                    CopyStateMachineBehavioursOnStateMachine(srcLayer.stateMachine, sm);

                var collected = new List<(string path, AnimatorState stateRef)>();
                if (srcLayer.stateMachine != null)
                    CollectStatesRecursive(srcLayer.stateMachine, "", collected);

                Debug.Log($"[ClipOnly] Layer '{srcLayer.name}': 收集到 {collected.Count} 个State.");

                var srcToNew = new Dictionary<AnimatorState, AnimatorState>();

                // 复制 “-> Exit” 的无条件 Exit 过渡（你原有逻辑）
                var exitTransitionsMap = new Dictionary<AnimatorState, AnimatorStateTransition[]>();

                // 新增：复制 “仅 hasExitTime、无任何 conditions 的 A->B” 过渡
                var exitTimeOnlyTransitionsMap = new Dictionary<AnimatorState, AnimatorStateTransition[]>();

                var cols = 4;
                for (int si = 0; si < collected.Count; si++)
                {
                    var tuple = collected[si];
                    var srcState = tuple.stateRef;
                    var col = si % cols;
                    var row = si / cols;

                    var pos = new Vector3(220 + col * 240, 80 + row * 80, 0);
                    var newState = sm.AddState(srcState.name, pos);

                    if (srcState.motion is BlendTree)
                    {
                        Debug.LogWarning($"[ClipOnly] 状态 '{tuple.path}' 使用 BlendTree, 将直接引用源对象。若需仅保留Clip请手动处理。");
                    }

                    // 基本属性
                    newState.motion = srcState.motion;
                    newState.speed = srcState.speed;
                    newState.speedParameterActive = srcState.speedParameterActive;
                    newState.speedParameter = srcState.speedParameter;
                    newState.mirror = srcState.mirror;
                    newState.mirrorParameterActive = srcState.mirrorParameterActive;
                    newState.mirrorParameter = srcState.mirrorParameter;
                    newState.cycleOffset = srcState.cycleOffset;
                    newState.cycleOffsetParameterActive = srcState.cycleOffsetParameterActive;
                    newState.cycleOffsetParameter = srcState.cycleOffsetParameter;
                    newState.timeParameterActive = srcState.timeParameterActive;
                    newState.timeParameter = srcState.timeParameter;
                    newState.iKOnFeet = srcState.iKOnFeet;
                    try { newState.writeDefaultValues = srcState.writeDefaultValues; } catch { }

                    // 复制 State 上的 StateMachineBehaviour（含序列化参数）
                    CopyStateMachineBehavioursOnState(srcState, newState);

                    srcToNew[srcState] = newState;

                    // 仅 Clip 状态考虑复制 “无条件 Exit” 过渡
                    if (IsClipMotion(srcState.motion))
                    {
                        var srcTransitions = srcState.transitions ?? Array.Empty<AnimatorStateTransition>();
                        var exitTransitions = srcTransitions
                            .Where(t => t != null && SafeIsExit(t) && IsUnconditional(t))
                            .ToArray();
                        if (exitTransitions.Length > 0)
                            exitTransitionsMap[srcState] = exitTransitions;
                    }

                    // 新增：收集 “仅 hasExitTime、无条件、指向 destinationState 的 A->B 过渡”
                    {
                        var srcTransitions = srcState.transitions ?? Array.Empty<AnimatorStateTransition>();
                        var exitTimeOnly = srcTransitions
                            .Where(t => t != null
                                        && IsExitTimeOnlyTransition(t)
                                        && t.destinationState != null)
                            .ToArray();

                        if (exitTimeOnly.Length > 0)
                            exitTimeOnlyTransitionsMap[srcState] = exitTimeOnly;
                    }
                }

                var srcDefault = srcLayer.stateMachine != null ? srcLayer.stateMachine.defaultState : null;
                if (srcDefault != null && srcToNew.TryGetValue(srcDefault, out var mappedDefault))
                {
                    sm.defaultState = mappedDefault;
                    Debug.Log($"[ClipOnly] Layer '{srcLayer.name}': 默认状态沿用 '{srcDefault.name}'.");
                }
                else
                {
                    if (collected.Count > 0)
                    {
                        var fallback = srcToNew[collected[0].stateRef];
                        sm.defaultState = fallback;
                        Debug.LogWarning($"[ClipOnly] Layer '{srcLayer.name}': 未找到可映射的默认状态，使用 '{fallback.name}'。");
                    }
                    else
                    {
                        Debug.LogWarning($"[ClipOnly] Layer '{srcLayer.name}': 该层无任何状态。");
                    }
                }

                // 复制 “-> Exit” 的无条件 Exit 过渡
                foreach (var kv in exitTransitionsMap)
                {
                    var srcState = kv.Key;
                    var newState = srcToNew[srcState];
                    var transitions = kv.Value;

                    foreach (var t in transitions)
                    {
                        var newT = newState.AddExitTransition();
                        try { CopyTransitionSettings(t, newT); }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"[ClipOnly] 复制 Exit 过渡设置失败: Layer '{srcLayer.name}', State '{srcState.name}': {e}");
                        }
                        Debug.Log($"[ClipOnly] 复制 无条件 Exit 过渡: Layer '{srcLayer.name}' State '{srcState.name}' -> Exit");
                    }
                }

                // 新增：复制 “ExitTime-only”的 State->State 过渡（A->B）
                foreach (var kv in exitTimeOnlyTransitionsMap)
                {
                    var srcFrom = kv.Key;
                    if (!srcToNew.TryGetValue(srcFrom, out var newFrom))
                        continue;

                    var srcTransitions = kv.Value;
                    foreach (var t in srcTransitions)
                    {
                        var srcTo = t.destinationState;
                        if (srcTo == null) continue;

                        if (!srcToNew.TryGetValue(srcTo, out var newTo))
                        {
                            Debug.LogWarning($"[ClipOnly] 跳过过渡(目标状态未映射): Layer '{srcLayer.name}', {srcFrom.name} -> {srcTo.name}");
                            continue;
                        }

                        try
                        {
                            var newT = newFrom.AddTransition(newTo);
                            CopyTransitionSettings(t, newT);

                            Debug.Log($"[ClipOnly] 复制 ExitTime-only 过渡: Layer '{srcLayer.name}' {srcFrom.name} -> {srcTo.name}");
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"[ClipOnly] 复制 ExitTime-only 过渡失败: Layer '{srcLayer.name}', {srcFrom.name} -> {srcTo.name}: {e}");
                        }
                    }
                }

                // 提示：由于“拍平”，子状态机上的 Behaviours 不复制
                if (srcLayer.stateMachine != null && (srcLayer.stateMachine.stateMachines?.Length ?? 0) > 0)
                {
                    Debug.LogWarning($"[ClipOnly] Layer '{srcLayer.name}': 已拍平子状态机；子状态机上的 StateMachineBehaviour 未复制。");
                }
            }
        }

        private static void CollectStatesRecursive(AnimatorStateMachine sm, string prefix, List<(string path, AnimatorState stateRef)> list)
        {
            if (sm == null) return;
            var pre = string.IsNullOrEmpty(prefix) ? "" : (prefix + "/");

            var states = sm.states ?? Array.Empty<ChildAnimatorState>();
            foreach (var child in states)
            {
                var path = pre + child.state.name;
                list.Add((path, child.state));
            }

            var subSms = sm.stateMachines ?? Array.Empty<ChildAnimatorStateMachine>();
            foreach (var sub in subSms)
            {
                var subPrefix = pre + sub.stateMachine.name;
                CollectStatesRecursive(sub.stateMachine, subPrefix, list);
            }
        }

        private static bool IsClipMotion(Motion motion) => motion is AnimationClip;

        private static bool SafeIsExit(AnimatorStateTransition t)
        {
            try { return t.isExit; } catch { return false; }
        }

        private static bool IsUnconditional(AnimatorStateTransition t)
        {
            var conds = t.conditions ?? Array.Empty<AnimatorCondition>();
            return conds.Length == 0;
        }

        // 新增：判定 “仅 hasExitTime，没有任何 conditions 的 State->State 过渡”
        private static bool IsExitTimeOnlyTransition(AnimatorStateTransition t)
        {
            if (t == null) return false;
            if (SafeIsExit(t)) return false;                 // 排除 -> Exit
            if (!t.hasExitTime) return false;                // 必须 hasExitTime = true
            var conds = t.conditions ?? Array.Empty<AnimatorCondition>();
            return conds.Length == 0;                        // 且无任何条件
        }

        private static void CopyTransitionSettings(AnimatorStateTransition src, AnimatorStateTransition dest)
        {
            dest.hasExitTime = src.hasExitTime;
            dest.exitTime = src.exitTime;
            dest.duration = src.duration;
            dest.offset = src.offset;
            try { dest.hasFixedDuration = src.hasFixedDuration; } catch { }
            try { dest.interruptionSource = src.interruptionSource; } catch { }
            try { dest.orderedInterruption = src.orderedInterruption; } catch { }
            try { dest.canTransitionToSelf = src.canTransitionToSelf; } catch { }
            try { dest.mute = src.mute; } catch { }
            try { dest.solo = src.solo; } catch { }

            var conds = src.conditions ?? Array.Empty<AnimatorCondition>();
            foreach (var c in conds)
                dest.AddCondition(c.mode, c.threshold, c.parameter);
        }

        // 新增：复制“状态”上的 StateMachineBehaviour 及其序列化字段
        private static void CopyStateMachineBehavioursOnState(AnimatorState srcState, AnimatorState destState)
        {
            var behaviours = srcState.behaviours ?? Array.Empty<StateMachineBehaviour>();
            foreach (var b in behaviours)
            {
                if (b == null) continue;
                try
                {
                    var newB = destState.AddStateMachineBehaviour(b.GetType());
                    EditorUtility.CopySerialized(b, newB); // 复制序列化字段（即 Inspector 参数）
                    try { newB.name = b.name; } catch { }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ClipOnly] 复制 StateMachineBehaviour 失败: State '{srcState.name}', Behaviour '{b?.GetType().Name}': {e}");
                }
            }
            if (behaviours.Length > 0)
                Debug.Log($"[ClipOnly] State '{srcState.name}': 已复制 {behaviours.Length} 个 StateMachineBehaviour.");
        }

        // 新增：复制“根状态机”上的 StateMachineBehaviour（不含子状态机）
        private static void CopyStateMachineBehavioursOnStateMachine(AnimatorStateMachine srcSm, AnimatorStateMachine destSm)
        {
            var behaviours = srcSm.behaviours ?? Array.Empty<StateMachineBehaviour>();
            foreach (var b in behaviours)
            {
                if (b == null) continue;
                try
                {
                    var newB = destSm.AddStateMachineBehaviour(b.GetType());
                    EditorUtility.CopySerialized(b, newB);
                    try { newB.name = b.name; } catch { }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ClipOnly] 复制 根状态机 Behaviour 失败: '{srcSm.name}', Behaviour '{b?.GetType().Name}': {e}");
                }
            }
            if (behaviours.Length > 0)
                Debug.Log($"[ClipOnly] StateMachine '{srcSm.name}': 已复制 {behaviours.Length} 个 StateMachineBehaviour.");
        }
    }
}
