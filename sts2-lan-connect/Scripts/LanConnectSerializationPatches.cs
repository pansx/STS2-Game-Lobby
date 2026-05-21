using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Sts2LanConnect.Scripts;

internal static class LanConnectSerializationPatches
{
    private static readonly Harmony HarmonyInstance = new("sts2_lan_connect.serialization");
    private static bool _applied;
    private static int _patchedCount;
    private static int _failedCount;

    private static readonly MethodInfo? WriteIntWithBits =
        AccessTools.Method(typeof(PacketWriter), nameof(PacketWriter.WriteInt), new[] { typeof(int), typeof(int) });

    private static readonly MethodInfo? ReadIntWithBits =
        AccessTools.Method(typeof(PacketReader), nameof(PacketReader.ReadInt), new[] { typeof(int) });

    private static readonly MethodInfo? WriteListWithBits =
        typeof(PacketWriter).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(static m => m.Name == nameof(PacketWriter.WriteList)
                && m.IsGenericMethodDefinition
                && m.GetParameters().Length == 2
                && m.GetParameters()[1].ParameterType == typeof(int));

    private static readonly MethodInfo? ReadListWithBits =
        typeof(PacketReader).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(static m => m.Name == nameof(PacketReader.ReadList)
                && m.IsGenericMethodDefinition
                && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == typeof(int));

    private static readonly MethodInfo? GetActiveSlotIdBitWidth =
        AccessTools.Method(typeof(LanConnectProtocolProfiles), nameof(LanConnectProtocolProfiles.GetActiveSlotIdBitWidth));

    private static readonly MethodInfo? GetActiveLobbyListBitWidth =
        AccessTools.Method(typeof(LanConnectProtocolProfiles), nameof(LanConnectProtocolProfiles.GetActiveLobbyListBitWidth));

    private static readonly FieldInfo? PacketReaderBitPositionField =
        AccessTools.Field(typeof(PacketReader), "<BitPosition>k__BackingField");

    public static void Apply()
    {
        if (_applied)
        {
            return;
        }

        _applied = true;

        if (LanConnectExternalModDetection.IsRmpModLoaded)
        {
            Log.Info("sts2_lan_connect serialization: RMP mod detected, skipping serialization patches.");
            return;
        }

        PatchLobbyPlayerSlotId();
        PatchClientLobbyJoinResponseList();
        PatchLobbyBeginRunList();

        Log.Info(
            $"sts2_lan_connect serialization: patches applied={_patchedCount}, failed={_failedCount}. " +
            $"activeProfile={LanConnectProtocolProfiles.GetActiveProfile()}, slotId=dynamic, lobbyList=dynamic");
    }

    private static void TrySafePatch(MethodInfo? target, string transpilerName, string label)
    {
        if (target == null)
        {
            Log.Warn($"sts2_lan_connect serialization: target method not found, skipping patch: {label}");
            _failedCount++;
            return;
        }

        try
        {
            HarmonyInstance.Patch(target, transpiler: new HarmonyMethod(
                typeof(LanConnectSerializationPatches), transpilerName));
            _patchedCount++;
        }
        catch (Exception ex)
        {
            Log.Error($"sts2_lan_connect serialization: failed to patch {label}: {ex}");
            _failedCount++;
        }
    }

    private static void TrySafePrefixPatch(MethodInfo? target, string prefixName, string label)
    {
        if (target == null)
        {
            Log.Warn($"sts2_lan_connect serialization: target method not found, skipping patch: {label}");
            _failedCount++;
            return;
        }

        try
        {
            HarmonyInstance.Patch(target, prefix: new HarmonyMethod(
                typeof(LanConnectSerializationPatches), prefixName));
            _patchedCount++;
        }
        catch (Exception ex)
        {
            Log.Error($"sts2_lan_connect serialization: failed to patch {label}: {ex}");
            _failedCount++;
        }
    }

    private static void PatchLobbyPlayerSlotId()
    {
        TrySafePatch(
            AccessTools.Method(typeof(LobbyPlayer), nameof(LobbyPlayer.Serialize)),
            nameof(TranspileLobbyPlayerSerialize),
            "LobbyPlayer.Serialize");
        TrySafePatch(
            AccessTools.Method(typeof(LobbyPlayer), nameof(LobbyPlayer.Deserialize)),
            nameof(TranspileLobbyPlayerDeserialize),
            "LobbyPlayer.Deserialize");
    }

    private static void PatchClientLobbyJoinResponseList()
    {
        TrySafePatch(
            AccessTools.Method(typeof(ClientLobbyJoinResponseMessage), nameof(ClientLobbyJoinResponseMessage.Serialize)),
            nameof(TranspileJoinResponseSerialize),
            "ClientLobbyJoinResponseMessage.Serialize");
        TrySafePatch(
            AccessTools.Method(typeof(ClientLobbyJoinResponseMessage), nameof(ClientLobbyJoinResponseMessage.Deserialize)),
            nameof(TranspileJoinResponseDeserialize),
            "ClientLobbyJoinResponseMessage.Deserialize");
    }

    private static void PatchLobbyBeginRunList()
    {
        TrySafePrefixPatch(
            AccessTools.Method(typeof(LobbyBeginRunMessage), nameof(LobbyBeginRunMessage.Serialize)),
            nameof(LobbyBeginRunSerializePrefix),
            "LobbyBeginRunMessage.Serialize");
        TrySafePrefixPatch(
            AccessTools.Method(typeof(LobbyBeginRunMessage), nameof(LobbyBeginRunMessage.Deserialize)),
            nameof(LobbyBeginRunDeserializePrefix),
            "LobbyBeginRunMessage.Deserialize");
    }

    // ReSharper disable UnusedMember.Local — invoked by Harmony via reflection

    private static IEnumerable<CodeInstruction> TranspileLobbyPlayerSerialize(IEnumerable<CodeInstruction> instructions)
        => LanConnectTranspilerUtils.ReplaceBitWidthBeforeCallWithProvider(instructions,
            WriteIntWithBits,
            LanConnectConstants.VanillaSlotIdBits,
            GetActiveSlotIdBitWidth,
            nameof(TranspileLobbyPlayerSerialize));

    private static IEnumerable<CodeInstruction> TranspileLobbyPlayerDeserialize(IEnumerable<CodeInstruction> instructions)
        => LanConnectTranspilerUtils.ReplaceBitWidthBeforeCallWithProvider(instructions,
            ReadIntWithBits,
            LanConnectConstants.VanillaSlotIdBits,
            GetActiveSlotIdBitWidth,
            nameof(TranspileLobbyPlayerDeserialize));

    private static IEnumerable<CodeInstruction> TranspileJoinResponseSerialize(IEnumerable<CodeInstruction> instructions)
        => LanConnectTranspilerUtils.ReplaceBitWidthBeforeCallWithProvider(instructions,
            WriteListWithBits,
            LanConnectConstants.VanillaLobbyListBits,
            GetActiveLobbyListBitWidth,
            nameof(TranspileJoinResponseSerialize));

    private static IEnumerable<CodeInstruction> TranspileJoinResponseDeserialize(IEnumerable<CodeInstruction> instructions)
        => LanConnectTranspilerUtils.ReplaceBitWidthBeforeCallWithProvider(instructions,
            ReadListWithBits,
            LanConnectConstants.VanillaLobbyListBits,
            GetActiveLobbyListBitWidth,
            nameof(TranspileJoinResponseDeserialize));

    private static bool LobbyBeginRunSerializePrefix(ref LobbyBeginRunMessage __instance, PacketWriter writer)
    {
        if (__instance.playersInLobby == null)
        {
            throw new InvalidOperationException("Tried to serialize LobbyBeginRunMessage with null player list.");
        }

        int listBits = LanConnectProtocolProfiles.GetActiveLobbyListBitWidth();
        int startBit = writer.BitPosition;
        writer.WriteList(__instance.playersInLobby, listBits);
        writer.WriteString(__instance.seed);
        writer.WriteList(__instance.modifiers);
        writer.WriteString(__instance.act1);
        Log.Info(
            $"sts2_lan_connect serialization: LobbyBeginRun serialize players={__instance.playersInLobby.Count} " +
            $"listBits={listBits} bits={startBit}->{writer.BitPosition}");
        return false;
    }

    private static bool LobbyBeginRunDeserializePrefix(ref LobbyBeginRunMessage __instance, PacketReader reader)
    {
        int activeBits = LanConnectProtocolProfiles.GetActiveLobbyListBitWidth();
        int fallbackBits = activeBits == LanConnectConstants.VanillaLobbyListBits
            ? LanConnectConstants.ExtendedLobbyListBits
            : LanConnectConstants.VanillaLobbyListBits;
        int startBit = reader.BitPosition;

        if (TryDeserializeLobbyBeginRun(ref __instance, reader, activeBits, startBit, "active", out Exception? activeException))
        {
            return false;
        }

        Exception activeFailure = activeException
            ?? new InvalidOperationException("LobbyBeginRun active deserialization failed without an exception.");

        if (TryDeserializeLobbyBeginRun(ref __instance, reader, fallbackBits, startBit, "fallback", out Exception? fallbackException))
        {
            Log.Warn(
                $"sts2_lan_connect serialization: LobbyBeginRun recovered using fallback listBits={fallbackBits} " +
                $"after active listBits={activeBits} failed: {activeFailure.GetType().Name}: {activeFailure.Message}");
            return false;
        }

        SetReaderBitPosition(reader, startBit);
        throw new InvalidOperationException(
            $"Failed to deserialize LobbyBeginRunMessage with active listBits={activeBits} or fallback listBits={fallbackBits}. " +
            $"Active error: {activeFailure.GetType().Name}: {activeFailure.Message}; " +
            $"Fallback error: {fallbackException?.GetType().Name}: {fallbackException?.Message}",
            activeFailure);
    }

    private static bool TryDeserializeLobbyBeginRun(
        ref LobbyBeginRunMessage message,
        PacketReader reader,
        int listBits,
        int startBit,
        string source,
        out Exception? exception)
    {
        SetReaderBitPosition(reader, startBit);
        try
        {
            message.playersInLobby = reader.ReadList<LobbyPlayer>(listBits);
            message.seed = reader.ReadString();
            message.modifiers = reader.ReadList<SerializableModifier>();
            message.act1 = reader.ReadString();
            exception = null;
            Log.Info(
                $"sts2_lan_connect serialization: LobbyBeginRun deserialize source={source} " +
                $"players={message.playersInLobby.Count} listBits={listBits} bits={startBit}->{reader.BitPosition}");
            return true;
        }
        catch (Exception ex)
        {
            exception = ex;
            Log.Warn(
                $"sts2_lan_connect serialization: LobbyBeginRun deserialize failed source={source} " +
                $"listBits={listBits}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void SetReaderBitPosition(PacketReader reader, int bitPosition)
    {
        PacketReaderBitPositionField?.SetValue(reader, bitPosition);
    }

    // ReSharper restore UnusedMember.Local
}
