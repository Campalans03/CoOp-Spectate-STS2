using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Players;
using SpectateMod.SpectateModCode.Net;

namespace SpectateMod.SpectateModCode;

/// <summary>
/// What each teammate's Crystal Sphere looks like, as they last told us over <see cref="zSpectateSphereMessage" />.
/// Only players who have the mod ever send it, so for anyone else there is simply nothing here.
/// </summary>
public static class SphereState
{
    /// <summary>
    /// One go at the sphere, from its screen opening to its closing. Its layout never changes; the rest is the latest
    /// they have sent. The mirror keeps hold of the same one for as long as it is up.
    /// </summary>
    public sealed class Session
    {
        public Session(zSpectateSphereMessage message)
        {
            Width = message.width;
            Height = message.height;
            Items = message.items.ToList();
            Revealed = new bool[Width * Height];
        }

        public int Width { get; }

        public int Height { get; }

        public IReadOnlyList<SpectateSphereItem> Items { get; }

        public bool[] Revealed { get; }

        public int Divinations { get; private set; }

        public bool SmallTool { get; private set; }

        /// <summary>The cell under their cursor, as an index into <see cref="Revealed" />, or -1.</summary>
        public int Hovered { get; private set; } = -1;

        public bool IsSameLayout(zSpectateSphereMessage message)
        {
            return message.width == Width && message.height == Height && message.items.Count == Items.Count
                   && message.items.Zip(Items).All(pair => IsSameItem(pair.First, pair.Second));
        }

        public void Update(zSpectateSphereMessage message)
        {
            for (int i = 0; i < Revealed.Length && i < message.revealed.Length; i++)
            {
                Revealed[i] = message.revealed[i];
            }
            Divinations = message.divinations;
            SmallTool = message.smallTool;
            Hovered = message.hovered < Revealed.Length ? message.hovered : -1;
        }

        private static bool IsSameItem(SpectateSphereItem a, SpectateSphereItem b)
        {
            return a.x == b.x && a.y == b.y && a.item.type == b.item.type && a.item.cardRarity == b.item.cardRarity
                   && a.item.potionRarity == b.item.potionRarity && a.item.isBigGold == b.item.isBigGold;
        }
    }

    private static readonly Dictionary<ulong, Session> sessions = new();

    public static Session? Get(Player player)
    {
        return sessions.GetValueOrDefault(player.NetId);
    }

    public static void Set(ulong netId, zSpectateSphereMessage message)
    {
        if (!message.open)
        {
            sessions.Remove(netId);
            return;
        }
        if (!sessions.TryGetValue(netId, out Session? session) || !session.IsSameLayout(message))
        {
            session = new Session(message);
            sessions[netId] = session;
        }
        session.Update(message);
    }

    public static void Clear()
    {
        sessions.Clear();
    }
}
