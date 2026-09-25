using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace SpectateMod.SpectateModCode.Net;

public enum SpectateDrawEventType
{
    None,
    BeginLine,
    ContinueLine,
    EndLine
}

/// <summary>One step of a stroke. Shaped like the game's NetMapDrawingEvent, minus the map's drawing modes.</summary>
public struct SpectateDrawEvent : IPacketSerializable
{
    public SpectateDrawEventType type;

    /// <summary>Resolution-independent position, from <see cref="NetCursorHelper.GetNormalizedPosition" />.</summary>
    public Vector2 position;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteEnum(type);
        if (type != SpectateDrawEventType.EndLine)
        {
            writer.WriteVector2(position, NetCursorHelper.quantizeParams, NetCursorHelper.quantizeParams);
        }
    }

    public void Deserialize(PacketReader reader)
    {
        type = reader.ReadEnum<SpectateDrawEventType>();
        if (type != SpectateDrawEventType.EndLine)
        {
            position = reader.ReadVector2(NetCursorHelper.quantizeParams, NetCursorHelper.quantizeParams);
        }
    }
}

/// <summary>
/// A spectator drawing on the screen of the player they are watching: a batch of stroke events, sent a few times a
/// second the way the map's drawings are.
/// <para>
/// <b>The lowercase name is deliberate; do not rename it.</b> The game numbers every message type, its own and the
/// mods', by sorting them on <c>Type.Name</c> with an ordinal compare, and puts that number on the wire. This mod does
/// not affect gameplay, so a lobby can mix players with and without it; a PascalCase name would slot in among the
/// game's own messages and shift the ids of everything after it, and the two sides would stop understanding each
/// other. Every game message starts with a capital, and lowercase sorts after uppercase, so this one always takes the
/// last id and leaves theirs alone. A player without the mod just logs an unknown id and drops it.
/// </para>
/// </summary>
// ReSharper disable once InconsistentNaming
public class zSpectateDrawMessage : INetMessage
{
    private const int listBits = 4;

    public static readonly int maxEventCount = (1 << listBits) - 1;

    private List<SpectateDrawEvent> _events = new();

    /// <summary>The player whose screen is drawn on. Everyone watching that screen sees it, including its owner.</summary>
    public ulong targetNetId;

    public IReadOnlyList<SpectateDrawEvent> Events => _events;

    /// <summary>Sent to the host, which passes it on to every other client.</summary>
    public bool ShouldBroadcast => true;

    /// <summary>Reliable, unlike the map: a lost Begin or End would leave a stroke joined to the wrong one.</summary>
    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.VeryDebug;

    public bool ShouldBuffer => true;

    public bool TryAddEvent(SpectateDrawEvent ev)
    {
        if (_events.Count >= maxEventCount)
        {
            return false;
        }
        _events.Add(ev);
        return true;
    }

    public void Serialize(PacketWriter writer)
    {
        writer.WriteULong(targetNetId);
        writer.WriteList(_events, listBits);
    }

    public void Deserialize(PacketReader reader)
    {
        targetNetId = reader.ReadULong();
        _events = reader.ReadList<SpectateDrawEvent>(listBits);
    }
}
