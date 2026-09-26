using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Players;

namespace SpectateMod.SpectateModCode;

/// <summary>
/// What each teammate's merchant rug looks like, as they last told us over <see cref="Net.zSpectateShopMessage" />.
/// Only players who have the mod ever send it, so for anyone else there is simply nothing here.
/// </summary>
public static class ShopState
{
    public readonly record struct Rug(bool Open, uint SoldMask)
    {
        public bool IsSold(int entryIndex)
        {
            return entryIndex is >= 0 and < 32 && (SoldMask & (1u << entryIndex)) != 0;
        }
    }

    private static readonly Dictionary<ulong, Rug> rugs = new();

    public static Rug Get(Player player)
    {
        return rugs.GetValueOrDefault(player.NetId);
    }

    public static void Set(ulong netId, Rug rug)
    {
        rugs[netId] = rug;
    }

    public static void Clear()
    {
        rugs.Clear();
    }
}
