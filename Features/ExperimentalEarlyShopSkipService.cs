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
/// Always-on experimental Harmony prefix for the three non-card shops.
/// The stable window fallback is disabled while testing these prefixes.
/// No hard Harmony reference: failure to resolve Harmony does not prevent
/// loading the card shop / ATM features.
/// For safety, only local-player actions with a confirmed positive price
/// greater than available gold are eligible to be skipped.
/// </summary>
internal sealed class ExperimentalEarlyShopSkipService {
    private const string Tag = "ShopSkip.EarlyTest";
    private const string PatchId = "ShopSkip.ExperimentalEarlyShopSkip.v1";
    private const string HarmonyLibrary = "0Harmony.dll";
    private const int MaxReasonablePrice = 100_000_000;

    private static ExperimentalEarlyShopSkipService? _active;
    private readonly HashSet<long> _completedActions = new HashSet<long>();
    private bool _attempted;
    private bool _installed;
    private bool _prefixSeen;
    private object? _harmony;
    private MethodInfo? _unpatch;
    private object[]? _unpatchArgs;

    internal static void Initialize() {
        SdkLog.Warn(Tag, "前置拦截实验已默认启用，所有对局模式均会尝试安装；三个商店的旧窗口取消逻辑暂停。" +
            "若补丁不兼容，商店会照常打开。请勿在正式多人对局中测试。" );
    }

    // Always suspend the old non-card window check. In particular, installation
    // failure must NOT be mistaken for a successful early prefix.
    internal bool IsExperimentSessionActive => true;

    internal void Tick() {
        // Attempt only once per scene, not once per frame. A failure is logged
        // and the game retains the original shop window behavior.
        if (_installed || _attempted) return;
        _attempted = true;
        SdkLog.Warn(Tag, "开始尝试安装三个商店的前置拦截（无单人模式与开关文件限制）。");
        TryInstall();
    }

    private void TryInstall() {
        try {
            string modDirectory = Path.Combine(SdkPath.ModsDirectory, "ShopSkip");
            // Do not put HarmonyLib types in fields, signatures or attributes.
            (Type harmonyType, Type methodType) = ResolveHarmonyTypes(modDirectory);
            SdkLog.Info(Tag, "使用 Harmony 程序集：" + harmonyType.Assembly.FullName);

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
                "请观察首次触发的 Prefix 日志；若无日志，商店正常打开且不会触发稳定自动取消。");
        } catch (Exception ex) {
            SdkLog.Warn(Tag, "前置拦截安装失败；仍暂停旧窗口取消，以便单独观察原版行为：" + ex.GetBaseException().Message);
            Uninstall();
        }
    }

    // Assembly.GetType(...) is unavailable in some stripped IL2CPP builds.
    // Look up assembly-qualified names via Type.GetType instead. This is a
    // separate runtime capability and might also be stripped in some builds.
    private static bool TryResolveTypes(out Type? harmony, out Type? harmonyMethod) {
        harmony = Type.GetType("HarmonyLib.Harmony, 0Harmony");
        harmonyMethod = Type.GetType("HarmonyLib.HarmonyMethod, 0Harmony");
        return harmony != null && harmonyMethod != null &&
               harmony.Assembly == harmonyMethod.Assembly;
    }

    private static (Type Harmony, Type HarmonyMethod) ResolveHarmonyTypes(string modDirectory) {
        // Type.GetType may return a type from a 0Harmony already loaded by
        // the loader. Do not load a second copy unless necessary.
        Type? harmony = null;
        Type? method = null;
        Exception? existingError = null;
        try {
            if (TryResolveTypes(out harmony, out method))
                return (harmony!, method!);
        } catch (Exception ex) {
            existingError = ex;
        }

        string dll = Path.Combine(modDirectory, HarmonyLibrary);
        if (!File.Exists(dll)) throw new FileNotFoundException(
            "无法找到 Harmony：运行时解析=" + (existingError?.GetBaseException().Message ?? "未解析到类型") +
            "；Mod 目录也没有 0Harmony.dll", dll);

        Exception? loadFromError = null;
        try {
            Assembly.LoadFrom(dll);
            if (TryResolveTypes(out harmony, out method))
                return (harmony!, method!);
        } catch (Exception ex) {
            loadFromError = ex;
        }

        Exception? loadBytesError = null;
        try {
            Assembly.Load(File.ReadAllBytes(dll));
            if (TryResolveTypes(out harmony, out method))
                return (harmony!, method!);
        } catch (Exception ex) {
            loadBytesError = ex;
        }

        throw new InvalidOperationException(
            "无法解析 Harmony 2 API（已绕过 Assembly.GetType）" +
            "; Loaded=" + (existingError?.GetBaseException().Message ?? "未解析到类型") +
            "; LoadFrom=" + (loadFromError?.GetBaseException().Message ?? "未解析到类型") +
            "; Load(bytes)=" + (loadBytesError?.GetBaseException().Message ?? "未解析到类型"));
    }

    private void Patch(MethodInfo patch, Type harmonyMethodType,
        Type windowType, string targetName, string prefixName) {
        MethodInfo? target = windowType.GetMethod(targetName,
            BindingFlags.Public | BindingFlags.Instance, null,
            new[] { typeof(BattlePlayerData), typeof(ShopAction) }, null);
        MethodInfo? prefix = typeof(ExperimentalEarlyShopSkipService).GetMethod(prefixName,
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
    /// True means: a real, local-player offer has been cancelled;
    /// Harmony should skip only the original UI opening method.
    /// Any incomplete or ambiguous state returns false (original runs).
    /// </summary>
    private bool TrySkip(BattlePlayerData? playerData, ShopAction? action,
        ShopKind kind, ref UniTask result) {
        if (!_installed) return false;
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
            SdkLog.Info(Tag, "提前跳过 " + kind + "：余额=" + gold +
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
