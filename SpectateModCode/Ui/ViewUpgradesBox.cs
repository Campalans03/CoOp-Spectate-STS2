using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.addons.mega_text;

namespace SpectateMod.SpectateModCode.Ui;

/// <summary>
/// The "View Upgrades" box from the bottom-left corner of the forge's upgrade screen, lifted out of it for screens that
/// don't have one.
/// <para>
/// The box is a piece of that screen's scene rather than a scene of its own, and its tickbox finds its picture by
/// unique name through the scene that owns it. So on its way out, everything the screen owned in it is handed to the
/// box itself, which then owns those names.
/// </para>
/// </summary>
public static class ViewUpgradesBox
{
    /// <summary>
    /// Puts a box in <paramref name="screen" />'s bottom-left corner, unticked, that reports each toggle. The screen
    /// must cover the whole window, as the game's screens do, since the box anchors itself to its corner.
    /// </summary>
    public static NTickbox? AddTo(Control screen, Action<bool> toggled)
    {
        Control source = PreloadManager.Cache.GetScene(NDeckUpgradeSelectScreen.ScenePath)
            .Instantiate<Control>(PackedScene.GenEditState.Disabled);
        try
        {
            if (source.GetNodeOrNull<Control>("ViewUpgrades") is not { } box
                || source.GetNodeOrNull<NTickbox>("%Upgrades") is not { } tickbox
                || source.GetNodeOrNull<MegaLabel>("%ViewUpgradesLabel") is not { } label)
            {
                MainFile.Logger.Error("The upgrade screen has no View Upgrades box to borrow");
                return null;
            }

            List<Node> owned = new();
            CollectOwned(box, source, owned);
            source.RemoveChild(box);
            foreach (Node node in owned)
            {
                node.Owner = box;
            }

            box.Name = "SpectateViewUpgrades";
            screen.AddChildSafely(box);
            label.SetTextAutoSize(new LocString("card_selection", "VIEW_UPGRADES").GetFormattedText());
            tickbox.IsTicked = false;
            tickbox.Toggled += t => Safe.Run("toggle View Upgrades", () => toggled(t.IsTicked));
            return tickbox;
        }
        finally
        {
            source.QueueFreeSafely();
        }
    }

    /// <summary>The nodes under <paramref name="node" /> that <paramref name="owner" /> owns, not counting itself.</summary>
    private static void CollectOwned(Node node, Node owner, List<Node> owned)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child.Owner == owner)
            {
                owned.Add(child);
            }
            CollectOwned(child, owner, owned);
        }
    }
}
