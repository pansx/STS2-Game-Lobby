using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
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
    private static Harmony? _harmony;
    private static bool _baseLibGuardPatched;
    private static bool _baseLibDetectionSubscribed;
    private static bool _baseLibMissingLogged;
    private static bool _baseLibGuardMissingLogged;

    public static void Apply(Harmony harmony)
    {
        _harmony = harmony;
        SubscribeBaseLibDetection();
        TryPatchBaseLibUnknownCharacterGuard("init", logMissing: false);
    }

    public static void RetryDeferredPatches(string source)
    {
        if (_harmony == null || _baseLibGuardPatched)
        {
            return;
        }

        TryPatchBaseLibUnknownCharacterGuard(source, logMissing: true);
    }

    private static void SubscribeBaseLibDetection()
    {
        if (_baseLibDetectionSubscribed)
        {
            return;
        }

        ModManager.OnModDetected += OnModDetected;
        _baseLibDetectionSubscribed = true;
    }

    private static void UnsubscribeBaseLibDetection()
    {
        if (!_baseLibDetectionSubscribed)
        {
            return;
        }

        ModManager.OnModDetected -= OnModDetected;
        _baseLibDetectionSubscribed = false;
    }

    private static void OnModDetected(Mod mod)
    {
        if (_harmony == null || _baseLibGuardPatched || !IsBaseLibMod(mod))
        {
            return;
        }

        TryPatchBaseLibUnknownCharacterGuard("mod_detected", logMissing: true);
    }

    private static bool IsBaseLibMod(Mod mod)
    {
        return string.Equals(mod.manifest?.id, "BaseLib", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mod.assembly?.GetName().Name, "BaseLib", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryPatchBaseLibUnknownCharacterGuard(string source, bool logMissing)
    {
        Type? guardType = AccessTools.TypeByName("BaseLib.Patches.Compatibility.UnknownCharacterPatches+IgnoreUnknownCoopRun")
            ?? AccessTools.TypeByName("BaseLib.Patches.Compatibility.IgnoreUnknownCoopRun")
            ?? AccessTools.TypeByName("BaseLib.Patches.Compatibility.UnknownCharacterPatches.IgnoreUnknownCoopRun");
        if (guardType == null)
        {
            LogMissingBaseLibGuard(source, logMissing);
            return false;
        }

        try
        {
            MethodInfo? guardMethod = AccessTools.Method(guardType, "SkipUnknownCharacter");
            if (guardMethod == null)
            {
                Log.Warn($"sts2_lan_connect save_manager: BaseLib unknown-character save guard method not found on type={guardType.FullName}.");
                return false;
            }

            _harmony!.Patch(
                guardMethod,
                prefix: new HarmonyMethod(typeof(LanConnectSaveManagerPatches), nameof(BaseLibSkipUnknownCharacterPrefix)));
            _baseLibGuardPatched = true;
            UnsubscribeBaseLibDetection();
            Log.Info($"sts2_lan_connect save_manager: patched BaseLib unknown-character save guard type={guardType.FullName}, source={source}.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"sts2_lan_connect save_manager: failed to patch BaseLib unknown-character save guard: {ex}");
            return false;
        }
    }

    private static void LogMissingBaseLibGuard(string source, bool logMissing)
    {
        if (!logMissing)
        {
            return;
        }

        bool baseLibLoaded = AppDomain.CurrentDomain.GetAssemblies()
            .Any(static assembly => string.Equals(assembly.GetName().Name, "BaseLib", StringComparison.OrdinalIgnoreCase));
        if (!baseLibLoaded)
        {
            if (!_baseLibMissingLogged)
            {
                _baseLibMissingLogged = true;
                Log.Info($"sts2_lan_connect save_manager: optional BaseLib compatibility patch skipped because BaseLib is not loaded, source={source}.");
            }

            return;
        }

        if (!_baseLibGuardMissingLogged)
        {
            _baseLibGuardMissingLogged = true;
            Log.Warn($"sts2_lan_connect save_manager: BaseLib is loaded but unknown-character save guard type was not found, source={source}.");
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
