using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace SpectateMod.SpectateModCode.Net;

/// <summary>
/// Whether a player has the merchant's rug rolled out, and which of its items are gone. Sent whenever either changes.
/// The base game shares neither: the rug is part of the room rather than a screen of its own, so the synced screen
/// stays "room" while it is open, and a purchase runs only on the buyer's machine, reaching the others as "obtained
/// this card" with their copy of the shop left fully stocked.
/// <para>
/// <b>Named to sort after <see cref="zSpectateHandSelectionMessage" />; keep it that way.</b> See the note on
/// <see cref="zSpectateDrawMessage" />: each new message has to sort last so the older ones keep their ids.
/// </para>
/// </summary>
// ReSharper disable once InconsistentNaming
public class zSpectateShopMessage : INetMessage
{
    public bool open;

    /// <summary>One bit per entry, in <c>MerchantInventory.AllEntries</c> order, set when it is no longer stocked.</summary>
    public uint soldMask;

    public bool ShouldBroadcast => true;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.VeryDebug;

    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteBool(open);
        writer.WriteUInt(soldMask);
    }

    public void Deserialize(PacketReader reader)
    {
        open = reader.ReadBool();
        soldMask = reader.ReadUInt();
    }
}
