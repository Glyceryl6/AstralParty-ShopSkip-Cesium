using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CesiumLoader.SDK;
using CesiumLoader.SDK.Logging;
using Core.Net;
using Cysharp.Threading.Tasks;
using GameLogic;
using party.protocol;
using UI;
using ShopAction = party.model.Action;

namespace ShopSkip.Features;

/// <summary>
/// Optional SINGLE-PLAYER-only prefix experiment. Never enabled by default.
/// No hard Harmony reference: an unsupported HybridCLR runtime simply keeps
/// the normal post-window ShopSkipService path intact.
///
/// Requires an opt-in marker named experimental-early-skip.enable next to the
/// mod DLL. An optional local 0Harmony.dll may be supplied for trial loading.
/// Removing the marker and restarting completely disables this feature.
/// </summary>
internal sealed class ExperimentalEarlyShopSkipService {
    private const string Tag = "ShopSkip.EarlyTest";
    private const string PatchId = "ShopSkip.ExperimentalEarlyShopSkip.v1";
    private const string EnableMarker = "experimental-early-skip.enable";
    private const string HarmonyLibrary = "0Harmony.dll";
    private const int MaxReasonablePrice = 100_000_000;

    private static ExperimentalEarlyShopSkipService? _active;
    private readonly HashSet<long> _completedActions = new HashSet<long>();
    private bool _enabled;
    private bool _attempted;
    private bool _installed;
    private bool _prefixSeen;
    private object? _harmony;
    private MethodInfo? _unpatch;
    private object[]? _unpatchArgs;

    internal void Initialize() {
        // SDK path discovery is essential in HybridCLR: Assembly.Location and
        // AppDomain.BaseDirectory may point to the wrong directory or be empty.
        try {
            string modDirectory = Path.Combine(SdkPath.ModsDirectory, "ShopSkip");
            _enabled = File.Exists(Path.Combine(modDirectory, EnableMarker));
            if (_enabled) SdkLog.Warn(Tag,
                "已发现实验开关。仅在单人模式尝试安装前置拦截；失败会保留原窗口取消功能。");
        } catch (Exception ex) {
            _enabled = false;
            SdkLog.Warn(Tag, "无法读取实验开关，保持关闭：" + ex.Message);
        }
    }

    internal void Tick() {
        if (!_enabled) return;
        if (_installed || _attempted) return;
        _attempted = true;
        TryInstall();
    }

    private void TryInstall() {
        try {
            string modDirectory = Path.Combine(SdkPath.ModsDirectory, "ShopSkip");
            Assembly? harmonyAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetType("HarmonyLib.Harmony", false) != null);
            if (harmonyAssembly == null) {
                string dll = Path.Combine(modDirectory, HarmonyLibrary);
                if (!File.Exists(dll)) {
                    SdkLog.Warn(Tag, "找不到已加载的 Harmony，也没有本地 0Harmony.dll。前置拦截未启用。");
                    return;
                }
                // Experimental only. Assembly.LoadFrom can itself be unsupported
                // by an IL2CPP/HybridCLR build; failure is handled here.
                harmonyAssembly = Assembly.LoadFrom(dll);
            }

            Type? harmonyType = harmonyAssembly.GetType("HarmonyLib.Harmony", false);
            Type? methodType = harmonyAssembly.GetType("HarmonyLib.HarmonyMethod", false);
            if (harmonyType == null || methodType == null)
                throw new InvalidOperationException("Harmony 类型不可用");

            _harmony = Activator.CreateInstance(harmonyType, new object[] { PatchId });
            if (_harmony == null) throw new InvalidOperationException("无法创建 Harmony 实例");
            ResolveUnpatch(harmonyType);
            if (_unpatch == null) throw new NotSupportedException("找不到仅卸载本 Mod 补丁的 API");

            MethodInfo? patch = harmonyType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Patch" &&
                    m.GetParameters().Length >= 2 &&
                    m.GetParameters()[0].ParameterType == typeof(MethodBase) &&
                    m.GetParameters()[1].ParameterType == methodType);
            if (patch == null) throw new NotSupportedException("当前 Harmony 没有可用的 Patch API");

            Patch(patch, methodType, typeof(LandRelicWindow), "ShowPurchase", nameof(RelicPrefix));
            Patch(patch, methodType, typeof(LandVendorWindow), "ShowVendorBuyCard", nameof(VendorPrefix));
            Patch(patch, methodType, typeof(LandBuildShopWindow), "ShowPurchase", nameof(BuildPrefix));

            _active = this;
            _installed = true;
            SdkLog.Warn(Tag, "已向 Harmony 注册三个商店入口（不代表 HybridCLR 已成功执行补丁）。" +
                "请观察首次触发的 Prefix 日志；无日志则仍使用原功能。");
        } catch (Exception ex) {
            SdkLog.Warn(Tag, "安装前置拦截失败，保持原版窗口处理：" + ex.GetBaseException().Message);
            Uninstall();
        }
    }

    private void Patch(MethodInfo patch, Type harmonyMethodType,
        Type windowType, string targetName, string prefixName) {
        MethodInfo? target = windowType.GetMethod(targetName,
            BindingFlags.Public | BindingFlags.Instance, null,
            new[] { typeof(BattlePlayerData), typeof(ShopAction) }, null);
        MethodInfo? prefix = GetType().GetMethod(prefixName,
            BindingFlags.NonPublic | BindingFlags.Static);
        if (target == null || target.ReturnType != typeof(UniTask) || prefix == null)
            throw new MissingMethodException(windowType.FullName, targetName + " / " + prefixName);

        object? patchDescriptor = Activator.CreateInstance(harmonyMethodType, new object[] { prefix });
        if (patchDescriptor == null) throw new InvalidOperationException("HarmonyMethod 创建失败");
        var parameters = patch.GetParameters();
        var args = new object?[parameters.Length];
        args[0] = target;
        args[1] = patchDescriptor;
        patch.Invoke(_harmony, args);
    }

    private void ResolveUnpatch(Type harmonyType) {
        // HarmonyX: UnpatchSelf() / static UnpatchID(id)
        // Harmony 2: UnpatchAll(id). Never invoke the global parameterless
        // UnpatchAll(): that would remove OTHER mods' patches.
        _unpatch = harmonyType.GetMethod("UnpatchSelf", BindingFlags.Public | BindingFlags.Instance,
            null, Type.EmptyTypes, null);
        _unpatchArgs = Array.Empty<object>();
        if (_unpatch != null) return;
        _unpatch = harmonyType.GetMethod("UnpatchID", BindingFlags.Public | BindingFlags.Static,
            null, new[] { typeof(string) }, null);
        _unpatchArgs = new object[] { PatchId };
        if (_unpatch != null) return;
        _unpatch = harmonyType.GetMethod("UnpatchAll", BindingFlags.Public | BindingFlags.Instance,
            null, new[] { typeof(string) }, null);
        _unpatchArgs = new object[] { PatchId };
    }

    private static bool RelicPrefix(BattlePlayerData playerData, ShopAction action, ref UniTask __result) {
        var active = _active;
        return active == null || !active.TrySkip(playerData, action, ShopKind.Relic, ref __result);
    }

    private static bool VendorPrefix(BattlePlayerData playerData, ShopAction action, ref UniTask __result) {
        var active = _active;
        return active == null || !active.TrySkip(playerData, action, ShopKind.Vendor, ref __result);
    }

    private static bool BuildPrefix(BattlePlayerData playerData, ShopAction action, ref UniTask __result) {
        var active = _active;
        return active == null || !active.TrySkip(playerData, action, ShopKind.Building, ref __result);
    }

    private enum ShopKind { Relic, Vendor, Building }

    /// <summary>
    /// True means: a real, local, single-player offer has been cancelled;
    /// Harmony should skip only the original UI opening method.
    /// Any incomplete or ambiguous state returns false (original runs).
    /// </summary>
    private bool TrySkip(BattlePlayerData? playerData, ShopAction? action,
        ShopKind kind, ref UniTask result) {
        if (!_installed || !_enabled) return false;
        if (!_prefixSeen) {
            _prefixSeen = true;
            SdkLog.Warn(Tag, "已进入前置 Prefix，说明当前运行环境确实触发了该方法补丁。");
        }
        try {
            if (playerData?.player == null || action == null || action.Sn <= 0 ||
                !GameLogicManager.hasInstance ||
                GameLogicManager.inst.account?.IsSelf(action.PlayerId) != true ||
                action.PlayerId != playerData.player.Id ||
                _completedActions.Contains(action.Sn)) return false;

            BattlePlayerData? self = GameLogicManager.inst.battle?.GetSelfPlayerData();
            if (self?.player == null || self.player.Id != playerData.player.Id) return false;
            int gold = self.Property.gold.Value;
            if (gold < 0 || action.Data == null) return false;

            int price;
            switch (kind) {
                case ShopKind.Relic:
                    price = ByteBuf.ReadObject<BuyRelicC2S>(action.Data.ToByteArray()).RelicGold;
                    break;
                case ShopKind.Vendor:
                    price = ByteBuf.ReadObject<VendorBuyCardC2S>(action.Data.ToByteArray()).Gold;
                    break;
                default:
                    price = ByteBuf.ReadObject<BuyPlantC2S>(action.Data.ToByteArray()).Gold;
                    break;
            }
            // 0 is a valid FREE price. Negative or wildly high values are
            // ambiguous; never skip an offer that might actually be buyable.
            if (price <= 0 || price > MaxReasonablePrice || gold >= price) return false;

            // Use exactly the original game's "Cancel" RPC (which also cancels
            // its operation timer and records the completed action Sn).
            switch (kind) {
                case ShopKind.Relic:
                    GameLogicManager.inst.land.RequestBuyRelicC2S(action.Sn, 0);
                    break;
                case ShopKind.Vendor:
                    GameLogicManager.inst.land.RequestVendorBuyCardC2S(action.Sn, isBuy: false);
                    break;
                default:
                    GameLogicManager.inst.land.RequestPurchaseBuildCardC2S(action.Sn, isBuy: false);
                    break;
            }
            _completedActions.Add(action.Sn);
            result = UniTask.CompletedTask;
            SdkLog.Info(Tag, "单人提前跳过 " + kind + "：余额=" + gold +
                "，价格=" + price + "，操作=" + action.Sn);
            return true;
        } catch (Exception ex) {
            SdkLog.Warn(Tag, "报价判断失败，交还原窗口处理：" + ex.GetBaseException().Message);
            return false;
        }
    }

    internal void ResetSession() {
        Uninstall();
        _attempted = false;
        _prefixSeen = false;
        _completedActions.Clear();
    }

    private void Uninstall() {
        // Stop our prefix before attempting removal; even if the runtime cannot
        // undo a detour, the method will then ALWAYS allow the original to run.
        _installed = false;
        _active = null;
        try {
            if (_unpatch != null)
                _unpatch.Invoke(_unpatch.IsStatic ? null : _harmony, _unpatchArgs);
        } catch (Exception ex) {
            SdkLog.Warn(Tag, "补丁卸载失败（已禁用逻辑），建议重启游戏：" + ex.GetBaseException().Message);
        } finally {
            _harmony = null;
            _unpatch = null;
            _unpatchArgs = null;
        }
    }
}
