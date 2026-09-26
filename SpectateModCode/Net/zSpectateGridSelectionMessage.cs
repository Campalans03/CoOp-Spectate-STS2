using System.Collections.Generic;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace SpectateMod.SpectateModCode.Net;

/// <summary>
/// Where a player is in a grid of cards they are picking from: which they have marked, as positions in the list the
/// screen was given, and how far down they have scrolled. The base game sends neither, only the final answer. An empty,
/// unscrolled message means the grid is closed.
/// <para>
/// Sorts after <see cref="zSpectateDrawMessage" />, the one message a released version of the mod has, so that one
/// keeps its id; see the note there. The messages added alongside this one have not shipped yet, so their order among
/// themselves doesn't matter. Once they have, a newer message must sort after all of them.
/// </para>
/// </summary>
// ReSharper disable once InconsistentNaming
public class zSpectateGridSelectionMessage : INetMessage
{
    /// <summary>A deck can be long; 10 bits covers far more than any grid will ever show.</summary>
    private const int indexBits = 10;

    private const int countBits = 10;

    public static readonly int maxMarked = (1 << countBits) - 1;

    public List<int> marked = new();

    public float scroll;

    public bool ShouldBroadcast => true;

    /// <summary>Reliable: a lost update would leave a card looking marked, or not, until the next one.</summary>
    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.VeryDebug;

    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt((uint)marked.Count, countBits);
        foreach (int index in marked)
        {
            writer.WriteUInt((uint)index, indexBits);
        }
        writer.WriteFloat(scroll);
    }

    public void Deserialize(PacketReader reader)
    {
        int count = (int)reader.ReadUInt(countBits);
        marked = new List<int>(count);
        for (int i = 0; i < count; i++)
        {
            marked.Add((int)reader.ReadUInt(indexBits));
        }
        scroll = reader.ReadFloat();
    }
}
