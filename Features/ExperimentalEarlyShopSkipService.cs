using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
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
/// Uses the referenced HarmonyLib API directly. The patch remains opt-in,
/// SINGLE-PLAYER-only, and preserves the normal post-window fallback.
///
/// Requires a compile-time reference to 0Harmony.dll and the same compatible
/// assembly at runtime. Create experimental-early-skip.enable next to the mod
/// DLL to opt in; remove it and restart to disable.
/// </summary>
internal sealed class ExperimentalEarlyShopSkipService {
    private const string Tag = "ShopSkip.EarlyTest";
    private const string PatchId = "ShopSkip.ExperimentalEarlyShopSkip.v1";
    private const int MaxReasonablePrice = 100_000_000;

    private static ExperimentalEarlyShopSkipService? _active;
    private readonly HashSet<long> _completedActions = [];
    private bool _enabled;
    private bool _attempted;
    private bool _installed;
    private bool _prefixSeen;
    private Harmony? _harmony;

    internal void Tick() {
        if (!_installed && !_attempted) {
            _attempted = true;
            TryInstall();
        }
    }

    private void TryInstall() {
        try {
            // Only patch these three windows. PatchAll(assembly) might also
            // install other modules' annotated patches, so avoid it here.
            _harmony = new Harmony(PatchId);
            InstallPatch(typeof(RelicPurchasePatch));
            InstallPatch(typeof(VendorPurchasePatch));
            InstallPatch(typeof(BuildPurchasePatch));

            _active = this;
            _installed = true;
            SdkLog.Warn(Tag, "Harmony 已注册三个商店入口（不代表 HybridCLR 已执行补丁）。" +
                "请观察首次触发的 Prefix 日志；没有日志则仍使用原功能。");
        } catch (Exception ex) {
            SdkLog.Warn(Tag, "安装前置拦截失败，保持原窗口处理：" + ex.GetBaseException().Message);
            // Also clean up a partial installation (e.g. only one window patched).
            Uninstall();
        }
    }

    private void InstallPatch(Type patchType) {
        var replacements = _harmony!.CreateClassProcessor(patchType).Patch();
        if (replacements == null || replacements.Count == 0)
            throw new InvalidOperationException("Harmony 未注册目标补丁：" + patchType.Name);
    }

    [HarmonyPatch(typeof(LandRelicWindow), nameof(LandRelicWindow.ShowPurchase),
        typeof(BattlePlayerData), typeof(ShopAction))]
    private static class RelicPurchasePatch {
        [HarmonyPrefix]
        private static bool Prefix(BattlePlayerData playerData, ShopAction action, ref UniTask __result) {
            var active = _active;
            return active == null || !active.TrySkip(playerData, action, ShopKind.Relic, ref __result);
        }
    }

    [HarmonyPatch(typeof(LandVendorWindow), nameof(LandVendorWindow.ShowVendorBuyCard),
        typeof(BattlePlayerData), typeof(ShopAction))]
    private static class VendorPurchasePatch {
        [HarmonyPrefix]
        private static bool Prefix(BattlePlayerData playerData, ShopAction action, ref UniTask __result) {
            var active = _active;
            return active == null || !active.TrySkip(playerData, action, ShopKind.Vendor, ref __result);
        }
    }

    [HarmonyPatch(typeof(LandBuildShopWindow), nameof(LandBuildShopWindow.ShowPurchase),
        typeof(BattlePlayerData), typeof(ShopAction))]
    private static class BuildPurchasePatch {
        [HarmonyPrefix]
        private static bool Prefix(BattlePlayerData playerData, ShopAction action, ref UniTask __result) {
            var active = _active;
            return active == null || !active.TrySkip(playerData, action, ShopKind.Building, ref __result);
        }
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
        // Disable our prefix logic first. On a detour-uninstall failure,
        // any surviving Prefix will continue the original method unchanged.
        _installed = false;
        _active = null;
        try {
            // Explicit owner ID: never remove another mod's Harmony patches.
            _harmony?.UnpatchSelf();
        } catch (Exception ex) {
            SdkLog.Warn(Tag, "补丁卸载失败（已禁用逻辑），建议重启游戏：" + ex.GetBaseException().Message);
        } finally {
            _harmony = null;
        }
    }
}
