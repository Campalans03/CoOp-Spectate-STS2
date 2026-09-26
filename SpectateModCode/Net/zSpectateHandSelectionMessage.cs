using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace SpectateMod.SpectateModCode.Net;

/// <summary>
/// The cards a player has marked so far while picking from their hand, sent whenever that changes; an empty list
/// means they are done. The base game only sends the final answer, so without this a spectator would see the cards
/// vanish on confirm without ever seeing them picked.
/// <para>
/// <b>Named to sort after <see cref="zSpectateDrawMessage" />; keep it that way.</b> Message ids come from sorting every
/// message type's name (see the note there). Sorting after the drawing message leaves the drawing message on the id
/// it had before this one existed, so players on an older version of the mod still draw on each other's screens; they
/// only drop this one as unknown.
/// </para>
/// </summary>
// ReSharper disable once InconsistentNaming
public class zSpectateHandSelectionMessage : INetMessage
{
    /// <summary>Up to 15 cards, past the 10 a hand can hold.</summary>
    private const int listBits = 4;

    public static readonly int maxCards = (1 << listBits) - 1;

    public List<NetCombatCard> cards = new();

    /// <summary>Sent to the host, which passes it on to every other client.</summary>
    public bool ShouldBroadcast => true;

    /// <summary>Reliable: a lost update would leave a card looking marked, or not, until the next one.</summary>
    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.VeryDebug;

    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteList(cards, listBits);
    }

    public void Deserialize(PacketReader reader)
    {
        cards = reader.ReadList<NetCombatCard>(listBits);
    }
}
