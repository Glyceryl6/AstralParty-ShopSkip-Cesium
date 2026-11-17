using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using FairyGUI;
using GameLogic;
using Google.Protobuf.Collections;
using party.protocol;
using UI;
using UnityEngine;

namespace ShopSkip.Features;

/// <summary>
/// Use the game's own Leave / Cancel handlers after confirming affordability.
/// Access UIManager's existing caches only: its public window getters create windows.
/// </summary>
internal sealed class ShopSkipService {
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private const int MaxReasonablePrice = 100_000_000;

    private static readonly FieldInfo? ShopCache = typeof(UIManager).GetField("_cachedLandShopWindow", Private);
    private static readonly FieldInfo? AtmCache = typeof(UIManager).GetField("_cachedATMWindow", Private);
    private static readonly FieldInfo? RelicCache = typeof(UIManager).GetField("_cacheLandRelicWindow", Private);
    private static readonly FieldInfo? VendorCache = typeof(UIManager).GetField("_cacheLandVendorWindow", Private);
    private static readonly FieldInfo? BuildShopCache = typeof(UIManager).GetField("_cachedLandBuildShopWindow", Private);
    private static readonly FieldInfo? ShoppingPlayer = typeof(LandShopWindow).GetField("shoppingPlayer", Private);
    private static readonly FieldInfo? CardGoods = typeof(LandShopWindow).GetField("CardGoods", Private);
    private static readonly FieldInfo? AlreadyCards = typeof(LandShopWindow).GetField("alreadyCards", Private);
    private static readonly FieldInfo? CardItems = typeof(LandShopWindow).GetField("cardItems", Private);

    private enum StockState { Unknown, Buyable, SoldOut, Unaffordable }

    private readonly HashSet<BaseWindow> _handledPurchases = [];
    private LandShopWindow? _handledShop;
    private long _handledAtmSn;
    private long _observedAtmSn;
    private float _observedAtmSince;
    private float _nextPoll;

    internal void Tick() {
        if (Time.realtimeSinceStartup < _nextPoll) return;
        _nextPoll = Time.realtimeSinceStartup + .22f;
        if (!UIManager.hasInstance) { Reset(); return; }
        try {
            UIManager manager = UIManager.inst;
            if (ShopCache?.GetValue(manager) is not LandShopWindow { isShowing: true } shop)
                _handledShop = null;
            else if (!ReferenceEquals(shop, _handledShop))
                TrySkipCards(shop);

            // A PVE transfer returns a new shop action while the ATM remains visible.
            // Evaluate the *refreshed server offer*, not prices cached before transfer.
            TrySkipAfterTransfer(AtmCache?.GetValue(manager) as ATMWindow);

            // Preserve the previous window-based fallback. We cannot reliably suppress
            // ShowPurchase/TryShow using the observational SDK event hooks.
            CheckPurchase(RelicCache?.GetValue(manager) as LandRelicWindow);
            CheckPurchase(VendorCache?.GetValue(manager) as LandVendorWindow);
            CheckPurchase(BuildShopCache?.GetValue(manager) as LandBuildShopWindow);
        } catch (Exception) {
            // A window or server action may still be initializing. Retry next poll.
        }
    }

    private void TrySkipCards(LandShopWindow window) {
        if (!GameLogicManager.hasInstance || ShoppingPlayer == null || CardGoods == null ||
            AlreadyCards == null || window.contentPane is not UILandShopWindow pane ||
            pane.btn_Leave is not { visible: true } || pane.btn_Leave.grayed) return;

        var land = GameLogicManager.inst.land;
        if (land?.CardShopAction == null || land.CardShopAction.Sn == 0L ||
            (land.CardShopAction.Id != 5029 && land.CardShopAction.Id != 5215)) return;
        var player = ShoppingPlayer.GetValue(window) as BattlePlayerData;
        var goods = CardGoods.GetValue(window) as List<(int, int)>;
        var sold = AlreadyCards.GetValue(window) as RepeatedField<bool>;
        if (player == null || goods == null || sold == null ||
            land.CardShopAction.PlayerId != player.player.Id) return;

        int gold = player.Property.gold.Value;
        var rendered = CardItems?.GetValue(window) as List<UILandShop_Button_Card>;
        StockState state = EvaluateVisibleShop(goods, sold, rendered, gold);
        if (state == StockState.Unknown || state == StockState.Buyable) return;

        if (state == StockState.SoldOut && land.CardShopAction.Id == 5215) {
            // A player with enough coins may still want to transfer to one teammate.
            PVEShopBuyC2S? offer = land.PVEShopData;
            if (offer == null || CanTransfer(offer, gold, pane.btn_Transfer)) return;
        }

        // Unaffordable inventory retains the prior auto-leave behavior. Sold-out
        // inventory leaves only when the remaining transfer option is unusable.
        _handledShop = window;
        pane.btn_Leave.onClick.Call();
    }

    private static StockState EvaluateVisibleShop(
        List<(int, int)> goods, RepeatedField<bool> sold,
        List<UILandShop_Button_Card>? rendered, int gold) {
        if (gold < 0 || goods.Count == 0 || goods.Count > 32 || sold.Count != goods.Count)
            return StockState.Unknown;

        bool hasUnsold = false;
        for (int i = 0; i < goods.Count; i++) {
            if (IsSold(i, sold, rendered)) continue;
            hasUnsold = true;
            int price = goods[i].Item2;
            // Zero-cost cards are legitimately free. Negative values can also
            // result from discounts: NEVER skip when their affordability is unclear.
            if (price < 0) return StockState.Buyable;
            if (price > MaxReasonablePrice) return StockState.Unknown;
            if (price <= gold) return StockState.Buyable;
        }
        return hasUnsold ? StockState.Unaffordable : StockState.SoldOut;
    }

    private static bool IsSold(int index, RepeatedField<bool> sold,
        List<UILandShop_Button_Card>? rendered) {
        if (sold[index]) return true;
        if (rendered == null) return false;
        // FinishShopBuy updates the rendered card's isSold flag but may not
        // update the old alreadyCards collection until the next shop offer.
        foreach (var card in rendered) {
            if (card?.data is int slot && slot == index && card.isSold.selectedIndex == 1)
                return true;
        }
        return false;
    }

    private static bool CanTransfer(PVEShopBuyC2S offer, int gold, GButton? button) {
        if (offer.AssistPlayer != 0L || button is not { visible: true } || button.grayed)
            return false;
        // Unknown/invalid transfer price: do not automatically dismiss the choice.
        return offer.AssistGold < 0 || gold >= offer.AssistGold;
    }

    private void TrySkipAfterTransfer(ATMWindow? atm) {
        if (atm is not { isShowing: true }) {
            _handledAtmSn = 0L;
            _observedAtmSn = 0L;
            return;
        }
        if (!GameLogicManager.hasInstance || atm.contentPane is not UIATMWindow pane ||
            pane.btn_Leave is not { visible: true } || pane.btn_Leave.grayed) return;

        var manager = GameLogicManager.inst;
        var land = manager.land;
        var action = land?.CardShopAction;
        PVEShopBuyC2S? offer = land?.PVEShopData;
        if (action == null || action.Id != 5215 || action.Sn == 0L ||
            action.Sn == _handledAtmSn ||
            offer == null || offer.AssistPlayer == 0L) return;

        BattlePlayerData? self = manager.battle?.GetSelfPlayerData();
        if (self == null || action.PlayerId != self.player.Id) return;
        // Allow the gold/offer UI to settle after receiving the new action.
        if (_observedAtmSn != action.Sn) {
            _observedAtmSn = action.Sn;
            _observedAtmSince = Time.realtimeSinceStartup;
            return;
        }
        if (Time.realtimeSinceStartup - _observedAtmSince < .25f) return;
        StockState state = EvaluatePveOffer(offer, self.Property.gold.Value);
        if (state != StockState.SoldOut && state != StockState.Unaffordable) return;

        // The ATM's native leave handler issues the required isClose request.
        // Only a new, post-transfer action Sn can be used here.
        _handledAtmSn = action.Sn;
        pane.btn_Leave.onClick.Call();
    }

    private static StockState EvaluatePveOffer(PVEShopBuyC2S offer, int gold) {
        if (gold < 0 || offer.Cards.Count == 0 || offer.Cards.Count > 32 ||
            offer.Alreadys.Count != offer.Cards.Count) return StockState.Unknown;

        int freePrice = 0;
        if (StaticConfigure.Skill.InfoDict.TryGetValue(10112, out var skill)) {
            foreach (int param in skill.Params) freePrice = param;
        }

        bool hasUnsold = false;
        for (int i = 0; i < offer.Cards.Count; i++) {
            if (offer.Alreadys[i]) continue;
            hasUnsold = true;
            bool discountedFree = offer.TalentSkillFreeCard.Count > i && offer.TalentSkillFreeCard[i];
            // Match LandShopWindow.UpdatePVECardShopData, including special free
            // card pricing. Use long math to avoid int overflow on bad data.
            long price = discountedFree
                ? Math.Max(0L, (long)freePrice - offer.DisCountGold)
                : (long)offer.Gold - offer.DisCountGold;
            if (price < 0) return StockState.Buyable;
            if (price > MaxReasonablePrice) return StockState.Unknown;
            if (price <= gold) return StockState.Buyable;
        }
        return hasUnsold ? StockState.Unaffordable : StockState.SoldOut;
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
        _handledShop = null;
        _handledAtmSn = 0L;
        _observedAtmSn = 0L;
        _observedAtmSince = 0f;
        _handledPurchases.Clear();
        _nextPoll = 0f;
    }
}
