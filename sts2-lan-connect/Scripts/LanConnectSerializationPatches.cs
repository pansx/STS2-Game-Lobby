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
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Multiplayer.Transport.ENet;
using MegaCrit.Sts2.Core.Multiplayer.Transport.Steam;
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
        PatchTransportSendMessageToClient();

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
        TrySafePrefixPatch(
            AccessTools.Method(typeof(ClientLobbyJoinResponseMessage), nameof(ClientLobbyJoinResponseMessage.Serialize)),
            nameof(ClientLobbyJoinResponseSerializePrefix),
            "ClientLobbyJoinResponseMessage.Serialize");
        TrySafePrefixPatch(
            AccessTools.Method(typeof(ClientLobbyJoinResponseMessage), nameof(ClientLobbyJoinResponseMessage.Deserialize)),
            nameof(ClientLobbyJoinResponseDeserializePrefix),
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

    private static void PatchTransportSendMessageToClient()
    {
        TrySafePrefixPatch(
            AccessTools.Method(typeof(ENetHost), nameof(ENetHost.SendMessageToClient)),
            nameof(HostSendMessageToClientPrefix),
            "ENetHost.SendMessageToClient");
        TrySafePrefixPatch(
            AccessTools.Method(typeof(SteamHost), nameof(SteamHost.SendMessageToClient)),
            nameof(HostSendMessageToClientPrefix),
            "SteamHost.SendMessageToClient");
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

    private static bool ClientLobbyJoinResponseSerializePrefix(ref ClientLobbyJoinResponseMessage __instance, PacketWriter writer)
    {
        if (__instance.playersInLobby == null)
        {
            throw new InvalidOperationException("Tried to serialize ClientLobbyJoinResponseMessage with null player list.");
        }

        int listBits = LanConnectProtocolProfiles.GetActiveLobbyListBitWidth();
        int slotBits = LanConnectProtocolProfiles.GetActiveSlotIdBitWidth();
        int startBit = writer.BitPosition;
        WriteLobbyPlayerList(writer, __instance.playersInLobby, listBits, slotBits);
        writer.WriteBool(__instance.dailyTime.HasValue);
        if (__instance.dailyTime.HasValue)
        {
            writer.Write(__instance.dailyTime.Value);
        }

        writer.WriteBool(__instance.seed != null);
        if (__instance.seed != null)
        {
            writer.WriteString(__instance.seed);
        }

        writer.WriteInt(__instance.ascension, 5);
        writer.WriteList(__instance.modifiers);
        Log.Info(
            $"sts2_lan_connect serialization: ClientLobbyJoinResponse serialize players={__instance.playersInLobby.Count} " +
            $"listBits={listBits} slotBits={slotBits} bits={startBit}->{writer.BitPosition} " +
            $"players=[{DescribeLobbyPlayers(__instance.playersInLobby)}]");
        return false;
    }

    private static bool ClientLobbyJoinResponseDeserializePrefix(ref ClientLobbyJoinResponseMessage __instance, PacketReader reader)
    {
        int activeBits = LanConnectProtocolProfiles.GetActiveLobbyListBitWidth();
        int fallbackBits = activeBits == LanConnectConstants.VanillaLobbyListBits
            ? LanConnectConstants.ExtendedLobbyListBits
            : LanConnectConstants.VanillaLobbyListBits;
        int startBit = reader.BitPosition;

        if (TryDeserializeClientLobbyJoinResponse(ref __instance, reader, activeBits, startBit, "active", out Exception? activeException))
        {
            return false;
        }

        Exception activeFailure = activeException
            ?? new InvalidOperationException("ClientLobbyJoinResponse active deserialization failed without an exception.");

        if (TryDeserializeClientLobbyJoinResponse(ref __instance, reader, fallbackBits, startBit, "fallback", out Exception? fallbackException))
        {
            Log.Warn(
                $"sts2_lan_connect serialization: ClientLobbyJoinResponse recovered using fallback listBits={fallbackBits} " +
                $"after active listBits={activeBits} failed: {activeFailure.GetType().Name}: {activeFailure.Message}");
            return false;
        }

        SetReaderBitPosition(reader, startBit);
        throw new InvalidOperationException(
            $"Failed to deserialize ClientLobbyJoinResponseMessage with active listBits={activeBits} or fallback listBits={fallbackBits}. " +
            $"Active error: {activeFailure.GetType().Name}: {activeFailure.Message}; " +
            $"Fallback error: {fallbackException?.GetType().Name}: {fallbackException?.Message}",
            activeFailure);
    }

    private static bool TryDeserializeClientLobbyJoinResponse(
        ref ClientLobbyJoinResponseMessage message,
        PacketReader reader,
        int listBits,
        int startBit,
        string source,
        out Exception? exception)
    {
        SetReaderBitPosition(reader, startBit);
        try
        {
            int slotBits = LanConnectProtocolProfiles.GetActiveSlotIdBitWidth();
            message.playersInLobby = ReadLobbyPlayerList(reader, listBits, slotBits);
            if (reader.ReadBool())
            {
                message.dailyTime = reader.Read<MegaCrit.Sts2.Core.Daily.TimeServerResult>();
            }
            else
            {
                message.dailyTime = null;
            }

            message.seed = reader.ReadBool() ? reader.ReadString() : null;
            message.ascension = reader.ReadInt(5);
            message.modifiers = reader.ReadList<SerializableModifier>();
            exception = null;
            Log.Info(
                $"sts2_lan_connect serialization: ClientLobbyJoinResponse deserialize source={source} " +
                $"players={message.playersInLobby.Count} listBits={listBits} slotBits={slotBits} " +
                $"bits={startBit}->{reader.BitPosition} players=[{DescribeLobbyPlayers(message.playersInLobby)}]");
            return true;
        }
        catch (Exception ex)
        {
            exception = ex;
            Log.Warn(
                $"sts2_lan_connect serialization: ClientLobbyJoinResponse deserialize failed source={source} " +
                $"listBits={listBits}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool LobbyBeginRunSerializePrefix(ref LobbyBeginRunMessage __instance, PacketWriter writer)
    {
        if (__instance.playersInLobby == null)
        {
            throw new InvalidOperationException("Tried to serialize LobbyBeginRunMessage with null player list.");
        }

        int listBits = LanConnectProtocolProfiles.GetActiveLobbyListBitWidth();
        int slotBits = LanConnectProtocolProfiles.GetActiveSlotIdBitWidth();
        int startBit = writer.BitPosition;
        WriteLobbyPlayerList(writer, __instance.playersInLobby, listBits, slotBits);
        writer.WriteString(__instance.seed);
        writer.WriteList(__instance.modifiers);
        writer.WriteString(__instance.act1);
        Log.Info(
            $"sts2_lan_connect serialization: LobbyBeginRun serialize players={__instance.playersInLobby.Count} " +
            $"listBits={listBits} slotBits={slotBits} bits={startBit}->{writer.BitPosition} " +
            $"seed={__instance.seed} act1={__instance.act1} players=[{DescribeLobbyPlayers(__instance.playersInLobby)}]");
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
            int slotBits = LanConnectProtocolProfiles.GetActiveSlotIdBitWidth();
            message.playersInLobby = ReadLobbyPlayerList(reader, listBits, slotBits);
            message.seed = reader.ReadString();
            message.modifiers = reader.ReadList<SerializableModifier>();
            message.act1 = reader.ReadString();
            exception = null;
            Log.Info(
                $"sts2_lan_connect serialization: LobbyBeginRun deserialize source={source} " +
                $"players={message.playersInLobby.Count} listBits={listBits} slotBits={slotBits} " +
                $"bits={startBit}->{reader.BitPosition} seed={message.seed} act1={message.act1} " +
                $"players=[{DescribeLobbyPlayers(message.playersInLobby)}]");
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

    private static void HostSendMessageToClientPrefix(ulong peerId, ref byte[] bytes, ref int length, NetTransferMode mode, int channel)
    {
        TryRewriteLobbyBeginRunPacket(peerId, ref bytes, ref length, mode, channel);
    }

    private static void TryRewriteLobbyBeginRunPacket(
        ulong peerId,
        ref byte[] bytes,
        ref int length,
        NetTransferMode mode,
        int channel)
    {
        int beginRunId = MessageTypes.TypeToId<LobbyBeginRunMessage>();
        PacketReader headerReader = new();
        headerReader.Reset(bytes);
        int messageId;
        ulong senderId;
        try
        {
            messageId = headerReader.ReadByte();
            if (messageId != beginRunId)
            {
                return;
            }

            senderId = headerReader.ReadULong();
        }
        catch (Exception ex)
        {
            Log.Warn(
                $"sts2_lan_connect serialization: failed to inspect outgoing packet peer={peerId} length={length}: " +
                $"{ex.GetType().Name}: {ex.Message}");
            return;
        }

        int bodyStartBit = headerReader.BitPosition;
        (LobbyBeginRunMessage? Message, string Source) parsed = TryReadLobbyBeginRunPacketBodyWithFallbacks(bytes, bodyStartBit);

        if (!parsed.Message.HasValue)
        {
            Log.Warn(
                $"sts2_lan_connect serialization: outgoing LobbyBeginRun packet detected but could not be parsed, " +
                $"leaving original bytes peer={peerId} sender={senderId} length={length} mode={mode} channel={channel}");
            return;
        }

        PacketWriter writer = new();
        writer.WriteByte((byte)beginRunId);
        writer.WriteULong(senderId);
        WriteLobbyBeginRunBody(writer, parsed.Message.Value);
        int oldLength = length;
        int oldBitLength = length * 8;
        bytes = writer.Buffer;
        length = (int)Math.Ceiling(writer.BitPosition / 8f);
        Log.Info(
            $"sts2_lan_connect serialization: rewrote outgoing LobbyBeginRun packet peer={peerId} sender={senderId} " +
            $"source={parsed.Source} length={oldLength}->{length} bits={oldBitLength}->{writer.BitPosition} " +
            $"mode={mode} channel={channel}");
    }

    private static (LobbyBeginRunMessage? Message, string Source) TryReadLobbyBeginRunPacketBodyWithFallbacks(
        byte[] bytes,
        int bodyStartBit)
    {
        int activeListBits = LanConnectProtocolProfiles.GetActiveLobbyListBitWidth();
        int activeSlotBits = LanConnectProtocolProfiles.GetActiveSlotIdBitWidth();
        (int ListBits, int SlotBits, string Source)[] candidates =
        {
            (activeListBits, activeSlotBits, "active"),
            (activeListBits, LanConnectConstants.VanillaSlotIdBits, "active-list/vanilla-slot"),
            (activeListBits, LanConnectConstants.Legacy4pSlotIdBits, "active-list/legacy-slot"),
            (LanConnectConstants.VanillaLobbyListBits, activeSlotBits, "vanilla-list/active-slot"),
            (LanConnectConstants.VanillaLobbyListBits, LanConnectConstants.VanillaSlotIdBits, "vanilla"),
            (LanConnectConstants.VanillaLobbyListBits, LanConnectConstants.Legacy4pSlotIdBits, "vanilla-list/legacy-slot")
        };

        foreach ((int listBits, int slotBits, string source) in candidates)
        {
            LobbyBeginRunMessage? parsed = TryReadLobbyBeginRunPacketBody(bytes, bodyStartBit, listBits, slotBits, source);
            if (parsed.HasValue)
            {
                return (parsed, source);
            }
        }

        return (null, "none");
    }

    private static LobbyBeginRunMessage? TryReadLobbyBeginRunPacketBody(
        byte[] bytes,
        int bodyStartBit,
        int listBits,
        int slotBits,
        string source)
    {
        PacketReader reader = new();
        reader.Reset(bytes);
        SetReaderBitPosition(reader, bodyStartBit);
        try
        {
            LobbyBeginRunMessage message = new()
            {
                playersInLobby = ReadLobbyPlayerList(reader, listBits, slotBits),
                seed = reader.ReadString(),
                modifiers = reader.ReadList<SerializableModifier>(),
                act1 = reader.ReadString()
            };
            Log.Info(
                $"sts2_lan_connect serialization: parsed outgoing LobbyBeginRun source={source} " +
                $"players={message.playersInLobby.Count} listBits={listBits} slotBits={slotBits} " +
                $"bits={bodyStartBit}->{reader.BitPosition} seed={message.seed} act1={message.act1} " +
                $"players=[{DescribeLobbyPlayers(message.playersInLobby)}]");
            return message;
        }
        catch (Exception ex)
        {
            Log.Warn(
                $"sts2_lan_connect serialization: parse outgoing LobbyBeginRun failed source={source} " +
                $"listBits={listBits}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static void WriteLobbyBeginRunBody(PacketWriter writer, LobbyBeginRunMessage message)
    {
        if (message.playersInLobby == null)
        {
            throw new InvalidOperationException("Tried to serialize LobbyBeginRunMessage with null player list.");
        }

        int listBits = LanConnectProtocolProfiles.GetActiveLobbyListBitWidth();
        int slotBits = LanConnectProtocolProfiles.GetActiveSlotIdBitWidth();
        int startBit = writer.BitPosition;
        WriteLobbyPlayerList(writer, message.playersInLobby, listBits, slotBits);
        writer.WriteString(message.seed);
        writer.WriteList(message.modifiers);
        writer.WriteString(message.act1);
        Log.Info(
            $"sts2_lan_connect serialization: LobbyBeginRun write body players={message.playersInLobby.Count} " +
            $"listBits={listBits} slotBits={slotBits} bits={startBit}->{writer.BitPosition} " +
            $"seed={message.seed} act1={message.act1} players=[{DescribeLobbyPlayers(message.playersInLobby)}]");
    }

    private static void SetReaderBitPosition(PacketReader reader, int bitPosition)
    {
        PacketReaderBitPositionField?.SetValue(reader, bitPosition);
    }

    private static void WriteLobbyPlayerList(PacketWriter writer, IReadOnlyList<LobbyPlayer> players, int listBits, int slotBits)
    {
        if ((ulong)players.Count >= (ulong)(1L << listBits))
        {
            throw new IndexOutOfRangeException($"Lobby player list length {players.Count} is too large to fit in bit size {listBits}.");
        }

        writer.WriteInt(players.Count, listBits);
        foreach (LobbyPlayer player in players)
        {
            WriteLobbyPlayer(writer, player, slotBits);
        }
    }

    private static List<LobbyPlayer> ReadLobbyPlayerList(PacketReader reader, int listBits, int slotBits)
    {
        int count = reader.ReadInt(listBits);
        List<LobbyPlayer> players = new(count);
        for (int i = 0; i < count; i++)
        {
            players.Add(ReadLobbyPlayer(reader, slotBits));
        }

        return players;
    }

    private static void WriteLobbyPlayer(PacketWriter writer, LobbyPlayer player, int slotBits)
    {
        writer.WriteULong(player.id);
        writer.WriteInt(player.slotId, slotBits);
        writer.WriteModel(player.character);
        writer.Write(player.unlockState);
        writer.WriteInt(player.maxMultiplayerAscensionUnlocked);
        writer.WriteBool(player.isReady);
    }

    private static LobbyPlayer ReadLobbyPlayer(PacketReader reader, int slotBits)
    {
        return new LobbyPlayer
        {
            id = reader.ReadULong(),
            slotId = reader.ReadInt(slotBits),
            character = reader.ReadModel<MegaCrit.Sts2.Core.Models.CharacterModel>(),
            unlockState = reader.Read<MegaCrit.Sts2.Core.Unlocks.SerializableUnlockState>(),
            maxMultiplayerAscensionUnlocked = reader.ReadInt(),
            isReady = reader.ReadBool()
        };
    }

    private static string DescribeLobbyPlayers(IEnumerable<LobbyPlayer> players)
    {
        return string.Join(", ", players.Select(static player =>
            $"{player.id}/slot={player.slotId}/char={player.character?.Id.ToString() ?? "<null>"}/ready={player.isReady}"));
    }

    // ReSharper restore UnusedMember.Local
}
