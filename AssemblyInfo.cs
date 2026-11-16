using CesiumLoader.SDK.Manifests;

[assembly: ModManifest("商店跳过", "0.1.0", "Glyceryl6",
    "在经过卡牌商店和筹码商店时，如果自身的星币不足以购买，则会自动跳过。",
    Permissions = ModPermission.ReadGameState, SdkVersion = "3.6.0")]
