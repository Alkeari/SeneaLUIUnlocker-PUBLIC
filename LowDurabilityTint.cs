using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;

namespace SeneaLUIUnlocker;

// SeneaL UI draws each player inventory cell as a Slot rooted on the game's own InventoryElement, so the tint is an Image of this plugin's own on that element and nothing of SeneaL UI is patched.
internal sealed class LowDurabilityTint
{
    private const string Section = "Reminders";
    private const string OverlayName = "SeneaL UI Unlocker Low Durability";
    // SeneaL UI's Slot background host. With [Inventory] SlotForeign on, SeneaL UI disables every unknown Image placed directly on the element, but leaves this host alone.
    private const string BackgroundHost = "slbg";

    private readonly ConfigEntry<bool> _on;
    private readonly ConfigEntry<float> _percent;
    private readonly ConfigEntry<float> _opacity;
    private readonly Dictionary<int, ItemDrop.ItemData> _items = [];
    private bool _shown;

    public LowDurabilityTint(ConfigFile cfg)
    {
        _on = cfg.Bind(Section, "Tint Low Durability Cells", true,
            "Tint the inventory cell of every item below Low Durability Percent in SeneaL UI's blood red.");
        _opacity = cfg.Bind(Section, "Low Durability Tint Opacity", 0.35f, new ConfigDescription(
            "How strong the tint over a worn item's cell is, from 0 (none) to 1 (solid).", new AcceptableValueRange<float>(0.05f, 1f)));
        cfg.TryGetEntry(Section, "Low Durability Percent", out _percent);
    }

    public void Tick()
    {
        var gui = InventoryGui.instance;
        if (gui == null)
        {
            _shown = false;
            return;
        }
        var grid = gui.m_playerGrid;
        var inv = grid == null ? null : grid.GetInventory();
        if (!_on.Value || !ServerRules.Allows("Reminders") || _percent == null || inv == null || !InventoryGui.IsVisible())
        {
            if (_shown && grid != null)
            {
                foreach (var element in grid.m_elements)
                {
                    Show(element, false);
                }
            }
            _shown = false;
            return;
        }
        _shown = true;
        var width = inv.GetWidth();
        _items.Clear();
        foreach (var item in inv.GetAllItems())
        {
            _items[item.m_gridPos.y * width + item.m_gridPos.x] = item;
        }
        var limit = _percent.Value / 100f;
        foreach (var element in grid.m_elements)
        {
            if (element == null)
            {
                continue;
            }
            var pos = element.Position;
            Show(element, _items.TryGetValue(pos.y * width + pos.x, out var item) && Low(item, limit));
        }
    }

    private static bool Low(ItemDrop.ItemData item, float limit)
    {
        if (!item.m_shared.m_useDurability)
        {
            return false;
        }
        var max = item.GetMaxDurability();
        return max > 0f && item.m_durability / max < limit;
    }

    private void Show(InventoryElement element, bool low)
    {
        if (element == null)
        {
            return;
        }
        var host = element.transform.Find(BackgroundHost);
        if (host == null)
        {
            host = element.transform;
        }
        var found = host.Find(OverlayName);
        if (found == null)
        {
            if (!low)
            {
                return;
            }
            found = Create(host);
        }
        var image = found.GetComponent<Image>();
        if (image != null && image.enabled != low)
        {
            image.enabled = low;
        }
        if (image != null && low && image.color.a != _opacity.Value)
        {
            var tint = SeneaLTheme.Color("Blood", new Color(0.79f, 0.29f, 0.24f));
            tint.a = _opacity.Value;
            image.color = tint;
        }
    }

    private static Transform Create(Transform host)
    {
        var go = new GameObject(OverlayName, typeof(RectTransform), typeof(Image));
        var rt = (RectTransform)go.transform;
        rt.SetParent(host, false);
        rt.SetAsLastSibling();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        var (sprite, pad) = SeneaLTheme.Panel("SlotEmpty", 64);
        rt.offsetMin = new Vector2(-pad, -pad);
        rt.offsetMax = new Vector2(pad, pad);
        var image = go.GetComponent<Image>();
        image.raycastTarget = false;
        if (sprite != null)
        {
            image.sprite = sprite;
            image.type = Image.Type.Sliced;
        }
        return rt;
    }
}
