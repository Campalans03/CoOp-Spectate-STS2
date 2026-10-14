using System.Collections.Generic;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace SpectateMod.SpectateModCode.Net;

/// <summary>One thing hidden in the Crystal Sphere: what it is, the way the game sends it, and its top-left cell.</summary>
public struct SpectateSphereItem : IPacketSerializable
{
    public SerializableCrystalSphereItem item;
    public int x;
    public int y;

    public void Serialize(PacketWriter writer)
    {
        writer.Write(item);
        writer.WriteInt(x, zSpectateSphereMessage.coordBits);
        writer.WriteInt(y, zSpectateSphereMessage.coordBits);
    }

    public void Deserialize(PacketReader reader)
    {
        item = reader.Read<SerializableCrystalSphereItem>();
        x = reader.ReadInt(zSpectateSphereMessage.coordBits);
        y = reader.ReadInt(zSpectateSphereMessage.coordBits);
    }
}

/// <summary>
/// A player's Crystal Sphere, the "?" event where you reveal cells to find what is hidden under them: where everything
/// is, which cells they have cleared, how many divinations they have left, the tool they are holding and the cell they
/// are pointing at. Sent whenever any of it changes. The base game shares none of it: the minigame only ever runs on
/// its player's machine, and the others only hear what it paid out once it is over.
/// <para>
/// Sorts after <see cref="zSpectateShopMessage" />, the last of the messages a released version of the mod has, so
/// theirs keep their ids; see the note on <see cref="zSpectateDrawMessage" />. A newer message must sort after this
/// one.
/// </para>
/// </summary>
// ReSharper disable once InconsistentNaming
public class zSpectateSphereMessage : INetMessage
{
    /// <summary>The game's grid is 11 cells a side.</summary>
    public const int coordBits = 5;

    private const int itemCountBits = 6;

    private const int countBits = 8;

    /// <summary>The game hides about fifteen things; this is all the list can carry.</summary>
    public static readonly int maxItems = (1 << itemCountBits) - 1;

    public bool open;

    public int width;

    public int height;

    public List<SpectateSphereItem> items = new();

    /// <summary>One per cell, row by row (<c>y * width + x</c>): true once its fog is gone.</summary>
    public bool[] revealed = System.Array.Empty<bool>();

    public int divinations;

    public bool smallTool;

    /// <summary>The cell under their cursor, as an index into <see cref="revealed" />, or -1.</summary>
    public int hovered = -1;

    public bool ShouldBroadcast => true;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.VeryDebug;

    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteBool(open);
        if (!open)
        {
            return;
        }
        writer.WriteInt(width, coordBits);
        writer.WriteInt(height, coordBits);
        writer.WriteList(items, itemCountBits);
        for (int i = 0; i < width * height; i++)
        {
            writer.WriteBool(i < revealed.Length && revealed[i]);
        }
        writer.WriteInt(divinations, countBits);
        writer.WriteBool(smallTool);
        writer.WriteBool(hovered >= 0);
        if (hovered >= 0)
        {
            writer.WriteInt(hovered, coordBits * 2);
        }
    }

    public void Deserialize(PacketReader reader)
    {
        open = reader.ReadBool();
        if (!open)
        {
            return;
        }
        width = reader.ReadInt(coordBits);
        height = reader.ReadInt(coordBits);
        items = reader.ReadList<SpectateSphereItem>(itemCountBits);
        revealed = new bool[width * height];
        for (int i = 0; i < revealed.Length; i++)
        {
            revealed[i] = reader.ReadBool();
        }
        divinations = reader.ReadInt(countBits);
        smallTool = reader.ReadBool();
        hovered = reader.ReadBool() ? reader.ReadInt(coordBits * 2) : -1;
    }
}
