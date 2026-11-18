using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using CesiumLoader.SDK.Logging;
using FairyGUI;
using GameLogic;
using UI;
using UnityEngine;

namespace ShopSkip.Features;

/// <summary>
/// Stable, window-based cancellation for relic, vendor and building shops.
/// Completely independent of the card shop/ATM and the experimental Harmony prefix.
/// The always-on experiment suspends this service even when patching fails,
/// so a normal Cancel click cannot be mistaken for a successful Prefix.
/// </summary>
internal sealed class ShopWindowFallbackService {
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly FieldInfo? RelicCache = typeof(UIManager).GetField("_cacheLandRelicWindow", Private);
    private static readonly FieldInfo? VendorCache = typeof(UIManager).GetField("_cacheLandVendorWindow", Private);
    private static readonly FieldInfo? BuildShopCache = typeof(UIManager).GetField("_cachedLandBuildShopWindow", Private);

    private readonly HashSet<BaseWindow> _handledPurchases = new HashSet<BaseWindow>();
    private float _nextPoll;
    private bool _suspended;

    internal void Tick(bool suspendForExperiment) {
        if (suspendForExperiment) {
            if (!_suspended) SdkLog.Warn("ShopSkip.WindowFallback",
                "已暂停三个商店的窗口内自动取消；现在只由 Harmony Prefix 决定是否提前跳过。");
            _suspended = true;
            _handledPurchases.Clear();
            _nextPoll = 0f;
            return;
        }
        if (_suspended) {
            _suspended = false;
            SdkLog.Info("ShopSkip.WindowFallback", "实验暂停已解除，恢复三个商店的窗口内自动取消。");
        }
        if (Time.realtimeSinceStartup < _nextPoll) return;
        _nextPoll = Time.realtimeSinceStartup + .22f;
        if (!UIManager.hasInstance) { Reset(); return; }
        try {
            UIManager manager = UIManager.inst;
            CheckPurchase(RelicCache?.GetValue(manager) as LandRelicWindow);
            CheckPurchase(VendorCache?.GetValue(manager) as LandVendorWindow);
            CheckPurchase(BuildShopCache?.GetValue(manager) as LandBuildShopWindow);
        } catch (Exception) {
            // A window or server offer can be in the middle of refreshing.
        }
    }

    private void CheckPurchase(BaseWindow? window) {
        if (window is not { isShowing: true }) {
            if (window != null) _handledPurchases.Remove(window);
            return;
        }
        if (_handledPurchases.Contains(window)) return;
        GRichTextField? priceText;
        GButton? cancel;
        switch (window.contentPane) {
            case UILandRelicWindow relic:
                priceText = relic.btn_Sure?.txt_Desc;
                cancel = relic.btn_Cancel;
                break;
            case UILandVendorWindow vendor:
                priceText = vendor.btn_Sure?.txt_Desc;
                cancel = vendor.btn_Cancel;
                break;
            case UILandBuildShopWindow build:
                priceText = build.btn_Sure?.txt_Desc;
                cancel = build.btn_Cancel;
                break;
            default: return;
        }
        TrySkipPurchase(window, priceText, cancel);
    }

    private void TrySkipPurchase(BaseWindow window, GRichTextField? priceLabel, GButton? cancel) {
        if (priceLabel == null || cancel is not { visible: true } || cancel.grayed ||
            !GameLogicManager.hasInstance) return;
        string html = priceLabel.richTextField?.htmlText ?? "";
        string visible = Regex.Replace(html, "<[^>]+>", "");
        MatchCollection numbers = Regex.Matches(visible, @"\d+");
        if (numbers.Count != 1 || !int.TryParse(numbers[0].Value, out int price) || price < 0) return;
        BattlePlayerData? self = GameLogicManager.inst.battle?.GetSelfPlayerData();
        if (self == null || self.Property.gold.Value >= price) return;
        _handledPurchases.Add(window);
        cancel.onClick.Call();
    }

    internal void Reset() {
        _handledPurchases.Clear();
        _nextPoll = 0f;
        _suspended = false;
    }
}
