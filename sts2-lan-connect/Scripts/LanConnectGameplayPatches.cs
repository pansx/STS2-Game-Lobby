using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;

namespace Sts2LanConnect.Scripts;

internal static class LanConnectGameplayPatches
{
    private static readonly Harmony HarmonyInstance = new("sts2_lan_connect.gameplay");
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        if (LanConnectExternalModDetection.IsRmpModLoaded)
        {
            Log.Info("sts2_lan_connect gameplay: RMP mod detected, skipping gameplay patches.");
            return;
        }

        var applied = 0;
        var failed = 0;

        if (TryApplyGroup("SaveManager", () => LanConnectSaveManagerPatches.Apply(HarmonyInstance))) applied++; else failed++;
        if (TryApplyGroup("DifficultyScaling", () => DifficultyScalingPatches.Apply(HarmonyInstance))) applied++; else failed++;
        if (TryApplyGroup("RestSite", () => RestSitePatches.Apply(HarmonyInstance))) applied++; else failed++;
        if (TryApplyGroup("Merchant", () => MerchantPatches.Apply(HarmonyInstance))) applied++; else failed++;
        if (TryApplyGroup("Treasure", () => TreasurePatches.Apply(HarmonyInstance))) applied++; else failed++;
        if (TryApplyGroup("LobbyCapacity", () => LanConnectLobbyCapacityPatches.Apply(HarmonyInstance))) applied++; else failed++;

        Log.Info($"sts2_lan_connect gameplay: patch groups applied={applied}, failed={failed}.");
    }

    public static void RetryDeferredPatches(string source)
    {
        if (!_initialized || LanConnectExternalModDetection.IsRmpModLoaded)
        {
            return;
        }

        TryApplyGroup($"deferred SaveManager ({source})", () => LanConnectSaveManagerPatches.RetryDeferredPatches(source));
    }

    private static bool TryApplyGroup(string groupName, Action apply)
    {
        try
        {
            apply();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"sts2_lan_connect gameplay: {groupName} patches failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
