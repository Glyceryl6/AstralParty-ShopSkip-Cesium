using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using FairyGUI;
using GameLogic;
using UI;
using UnityEngine;

namespace ShopSkip.Features;

/// <summary>
/// Skip only confirmed unaffordable purchases. Never obtain UIManager
/// window properties here: the game's property getters instantiate windows.
/// </summary>
internal sealed class ShopSkipService {
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly FieldInfo? ShopCache = typeof(UIManager).GetField("_cachedLandShopWindow", Private);
    private static readonly FieldInfo? RelicCache = typeof(UIManager).GetField("_cacheLandRelicWindow", Private);
    private static readonly FieldInfo? VendorCache = typeof(UIManager).GetField("_cacheLandVendorWindow", Private);
    private static readonly FieldInfo? BuildShopCache = typeof(UIManager).GetField("_cachedLandBuildShopWindow", Private);
    private static readonly FieldInfo? ShoppingPlayer = typeof(LandShopWindow).GetField("shoppingPlayer", Private);
    private static readonly FieldInfo? CardGoods = typeof(LandShopWindow).GetField("CardGoods", Private);
    private static readonly FieldInfo? AlreadyCards = typeof(LandShopWindow).GetField("alreadyCards", Private);

    private readonly HashSet<BaseWindow> _handledPurchases = [];
    private LandShopWindow? _handledShop;
    private float _nextPoll;

    internal void Tick() {
        if (Time.realtimeSinceStartup < _nextPoll) return;
        _nextPoll = Time.realtimeSinceStartup + .22f;
        if (!UIManager.hasInstance) { Reset(); return; }
        try {
            UIManager manager = UIManager.inst;
            if (ShopCache?.GetValue(manager) is not LandShopWindow { isShowing: true } shop) _handledShop = null;
            else if (!ReferenceEquals(shop, _handledShop)) TrySkipCards(shop);
            // All three optional land-purchase windows share the same
            // native confirmation/cancellation flow and price text.
            CheckPurchase(RelicCache?.GetValue(manager) as LandRelicWindow);
            CheckPurchase(VendorCache?.GetValue(manager) as LandVendorWindow);
            CheckPurchase(BuildShopCache?.GetValue(manager) as LandBuildShopWindow);
        } catch (Exception) {
            // The original window may be midway through initialization.
            // Preserve game behavior and retry the next poll.
        }
    }

    private void TrySkipCards(LandShopWindow window) {
        if (ShoppingPlayer == null || CardGoods == null || AlreadyCards == null ||
            window.contentPane is not UILandShopWindow pane) return;
        var player = ShoppingPlayer.GetValue(window) as BattlePlayerData;
        var goods = CardGoods.GetValue(window) as List<(int, int)>;
        var sold = AlreadyCards.GetValue(window) as Google.Protobuf.Collections.RepeatedField<bool>;
        if (player == null || goods == null || goods.Count == 0 || 
            (sold != null && goods.Count > sold.Count) || goods.Count > 32 
            || pane.btn_Leave is not { visible: true } || pane.btn_Leave.grayed) return;
        int gold = player.Property.gold.Value;
        if (gold < 0) return;
        int available = 0;
        for (int i = 0; i < goods.Count; i++) {
            if (sold != null && sold[i]) continue;
            int cost = goods[i].Item2;
            if (cost is < 0 or > 100000000) return;
            available++;
            if (cost <= gold) return;
        }
        if (available == 0) return;
        _handledShop = window;
        pane.btn_Leave.onClick.Call();
    }

    private void CheckPurchase(BaseWindow? window) {
        if (window is not { isShowing: true }) {
            if (window != null) _handledPurchases.Remove(window);
            return;
        }

        if (_handledPurchases.Contains(window)) return;
        GRichTextField? priceText;
        GButton? cancel;
        // Only the generated native fields differ between these windows.
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
            default:
                return;
        }
        
        TrySkipPurchase(window, priceText, cancel);
    }

    private void TrySkipPurchase(BaseWindow window, GRichTextField? priceLabel, GButton? cancel) {
        if (priceLabel == null || cancel is not { visible: true } || cancel.grayed || !GameLogicManager.hasInstance) return;
        // Like the old MOD, read only the already FlushVars-rendered cost.
        // Do not infer a price from uninitialized template placeholders.
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
        _handledShop = null;
        _handledPurchases.Clear();
        _nextPoll = 0f;
    }
        
}