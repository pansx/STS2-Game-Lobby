using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Sts2LanConnect.Scripts;

internal static class LanConnectSaveManagerPatches
{
    private static readonly FieldInfo? RunSaveManagerField = typeof(SaveManager).GetField("_runSaveManager", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo? LoadMultiplayerRunSaveMethod = RunSaveManagerField?.FieldType.GetMethod("LoadMultiplayerRunSave", BindingFlags.Instance | BindingFlags.Public);

    public static void Apply(Harmony harmony)
    {
        PatchBaseLibUnknownCharacterGuard(harmony);
    }

    private static void PatchBaseLibUnknownCharacterGuard(Harmony harmony)
    {
        Type? guardType = AccessTools.TypeByName("BaseLib.Patches.Compatibility.UnknownCharacterPatches+IgnoreUnknownCoopRun")
            ?? AccessTools.TypeByName("BaseLib.Patches.Compatibility.IgnoreUnknownCoopRun")
            ?? AccessTools.TypeByName("BaseLib.Patches.Compatibility.UnknownCharacterPatches.IgnoreUnknownCoopRun");
        if (guardType == null)
        {
            return;
        }

        try
        {
            MethodInfo? guardMethod = AccessTools.Method(guardType, "SkipUnknownCharacter");
            if (guardMethod == null)
            {
                Log.Warn($"sts2_lan_connect save_manager: BaseLib unknown-character save guard method not found on type={guardType.FullName}.");
                return;
            }

            harmony.Patch(
                guardMethod,
                prefix: new HarmonyMethod(typeof(LanConnectSaveManagerPatches), nameof(BaseLibSkipUnknownCharacterPrefix)));
            Log.Info($"sts2_lan_connect save_manager: patched BaseLib unknown-character save guard type={guardType.FullName}.");
        }
        catch (Exception ex)
        {
            Log.Error($"sts2_lan_connect save_manager: failed to patch BaseLib unknown-character save guard: {ex}");
        }
    }

    [HarmonyPriority(Priority.First)]
    private static bool BaseLibSkipUnknownCharacterPrefix([HarmonyArgument(1)] ref bool saveManagerGetterResult)
    {
        if (!CanLoadCurrentMultiplayerRunWithLanIdentity())
        {
            return true;
        }

        saveManagerGetterResult = true;
        return false;
    }

    private static bool CanLoadCurrentMultiplayerRunWithLanIdentity()
    {
        ReadSaveResult<SerializableRun> readResult = LoadRawCurrentMultiplayerRun();
        if (!readResult.Success || readResult.SaveData == null)
        {
            return false;
        }

        ulong localPlayerId = ResolveCanonicalLocalPlayerId(readResult.SaveData);
        try
        {
            _ = RunManager.CanonicalizeSave(readResult.SaveData, localPlayerId);
            return true;
        }
        catch (Exception ex)
        {
            GD.Print(
                $"sts2_lan_connect save_manager: LAN multiplayer save guard declined localPlayerId={localPlayerId}, playerIds={string.Join(',', readResult.SaveData.Players.Select(static player => player.NetId))}, reason={ex.Message}");
            return false;
        }
    }

    private static ReadSaveResult<SerializableRun> LoadRawCurrentMultiplayerRun()
    {
        try
        {
            if (RunSaveManagerField?.GetValue(SaveManager.Instance) is not object runSaveManager || LoadMultiplayerRunSaveMethod == null)
            {
                return new ReadSaveResult<SerializableRun>(ReadSaveStatus.Unrecoverable, "RunSaveManager reflection unavailable.");
            }

            object? result = LoadMultiplayerRunSaveMethod.Invoke(runSaveManager, Array.Empty<object>());
            return result as ReadSaveResult<SerializableRun>
                ?? new ReadSaveResult<SerializableRun>(ReadSaveStatus.Unrecoverable, "LoadMultiplayerRunSave returned unexpected result.");
        }
        catch (Exception ex)
        {
            return new ReadSaveResult<SerializableRun>(ReadSaveStatus.Unrecoverable, ex.Message);
        }
    }

    private static ulong ResolveCanonicalLocalPlayerId(SerializableRun run)
    {
        INetGameService? netService = RunManager.Instance.NetService;
        if (RunManager.Instance.IsInProgress
            && netService != null
            && netService.Type.IsMultiplayer()
            && netService.Platform == PlatformType.None
            && netService.IsConnected)
        {
            return netService.NetId;
        }

        ulong platformLocalPlayerId = PlatformUtil.GetLocalPlayerId(PlatformUtil.PrimaryPlatform);
        if (run.Players.Any(player => player.NetId == platformLocalPlayerId))
        {
            return platformLocalPlayerId;
        }

        if (PlatformUtil.PrimaryPlatform == PlatformType.None)
        {
            SerializablePlayer? hostPlayer = run.Players.FirstOrDefault(player => player.NetId == 1UL);
            if (hostPlayer != null)
            {
                return hostPlayer.NetId;
            }
        }

        return run.Players.First().NetId;
    }
}
