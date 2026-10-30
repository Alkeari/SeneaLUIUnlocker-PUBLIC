using System.Collections.Generic;
using System.Linq;
using SeneaLUI.Components;
using SeneaLUI.Core;
using SeneaLUI.Inv;
using SeneaLUI.Map;
using SeneaLUI.Patches;
using SeneaLUI.Theme;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SeneaLUIUnlocker;

internal interface IPinChips
{
    void Tick();
}

// Also turns SeneaL UI's arrow for another player (a Player pin) to the way that player faces, where the game has the player loaded; SeneaL UI draws every player arrow pointing north.
// A chip for each kind of auto-pin (PinKinds) on SeneaL UI's world map, drawn like SeneaL UI's own chips in a second row under them: a click shows or hides that kind, the count is how many of its pins the map has. Its pins get the kind's own picture from SeneaL UI's icons.
internal sealed class PinChips : IPinChips
{
    private const float ChipH = 28f;
    private readonly List<Chip> _chips = [];
    private MapView _view;
    private readonly Tip _tip = new();

    private sealed class Chip
    {
        public RectTransform Root;
        public Image Bg;
        public Image Icon;
        public TextMeshProUGUI Text;
        public TextMeshProUGUI Count;
        public bool Hover;
        public int Last = -1;
    }

    public static IPinChips Create() => new PinChips();

    public void Tick()
    {
        var view = MapPatches.View;
        var map = Minimap.instance;
        if (view == null || map == null || view._overlay == null)
        {
            return;
        }
        if (view != _view || _chips.Count == 0 || _chips[0].Root == null)
        {
            Build(view);
        }
        Icons(map);
        Facing(map);
        var counts = new int[PinKinds.Names.Length];
        foreach (var pin in map.m_pins)
        {
            var i = PinKinds.IndexOf(pin.m_type);
            if (i >= 0)
            {
                counts[i]++;
            }
        }
        var total = 0f;
        for (var i = 0; i < _chips.Count; i++)
        {
            var chip = _chips[i];
            var shown = ServerRules.Allows("AutoPins") && (counts[i] > 0 || !PinKinds.Shown(i));
            if (chip.Root.gameObject.activeSelf != shown)
            {
                chip.Root.gameObject.SetActive(shown);
            }
            if (!shown)
            {
                continue;
            }
            if (counts[i] != chip.Last)
            {
                chip.Last = counts[i];
                chip.Count.text = counts[i].ToString();
                var x = 28f + Ui.Measure(chip.Text, chip.Text.text).x + 6f;
                chip.Count.rectTransform.anchoredPosition = new Vector2(x, chip.Count.rectTransform.anchoredPosition.y);
                chip.Root.sizeDelta = new Vector2(x + Mathf.Max(view._countW, Ui.Measure(chip.Count, chip.Count.text).x) + 10f, ChipH);
            }
            Paint(chip, i);
            total += chip.Root.sizeDelta.x + (total > 0f ? 6f : 0f);
        }
        var left = -total * 0.5f;
        foreach (var chip in _chips.Where(chip => chip.Root.gameObject.activeSelf))
        {
            chip.Root.anchoredPosition = new Vector2(left + chip.Root.sizeDelta.x * 0.5f, -16f - ChipH - 6f);
            left += chip.Root.sizeDelta.x + 6f;
        }
    }

    private void Build(MapView view)
    {
        foreach (var chip in _chips.Where(chip => chip.Root != null))
        {
            Object.Destroy(chip.Root.gameObject);
        }
        _chips.Clear();
        _view = view;
        for (var i = 0; i < PinKinds.Names.Length; i++)
        {
            var index = i;
            var chip = new Chip { Root = Ui.TC(view._overlay, "unlocker.chip" + i, 0f, 16f + ChipH + 6f, 100f, ChipH) };
            chip.Bg = Ui.Skinned(chip.Root, Skin.Sliced(28, Styles.Chip), Styles.Chip.Pad, sliced: true);
            Ui.Img(chip.Root, null, Palette.Clear).raycastTarget = true;
            var input = chip.Root.gameObject.AddComponent<UIInputHandler>();
            input.m_onPointerEnter = _ =>
            {
                chip.Hover = true;
                Paint(chip, index);
            };
            input.m_onPointerExit = _ =>
            {
                chip.Hover = false;
                Paint(chip, index);
            };
            input.m_onLeftClick = _ =>
            {
                Click.Play();
                PinKinds.SetShown(index, !PinKinds.Shown(index));
                PinKinds.Tick();
                Paint(chip, index);
            };
            // A right-click lists the kind's pins in SeneaL UI's own pin list, as its own chips do from SeneaL UI 1.1.8.
            input.m_onRightClick = _ =>
            {
                var list = _view == null ? null : _view.List;
                if (list == null || Minimap.instance == null || !HasPinList)
                {
                    return;
                }
                Click.Play();
                ShowList(list, index, chip.Root);
            };
            Tooltip.Bind(input, chip.Root, () => Tips.Text(_tip, PinKinds.Names[index], (PinKinds.Shown(index) ? Loc.ChipOnTip : Loc.ChipOffTip) + (HasPinList ? "\n\n" + ListHint() : "")));
            chip.Icon = Ui.Icon(chip.Root, "i", SeneaLUI.Theme.Icons.Get(PinKinds.Icons[i]), Palette.Brass, 0f, 0.5f, 8f, 0f, 14f);
            chip.Icon.preserveAspect = true;
            var style = TextStyles.ChipText;
            chip.Text = Ui.Label(chip.Root, "t", style, 0f, 28f, (ChipH - style.Size * 1.3f) * 0.5f + style.Baseline(1.3f), 200f, TextAlignmentOptions.TopLeft);
            chip.Text.text = PinKinds.Names[i];
            chip.Count = Ui.Label(chip.Root, "n", TextStyles.ChipCount, 0f, 0f, (ChipH - style.Size * 1.3f) * 0.5f + style.Baseline(1.3f), 60f, TextAlignmentOptions.TopLeft);
            _chips.Add(chip);
        }
    }

    // SeneaL UI lists a chip's pins on right-click from 1.1.8; 1.1.7 has no such list, so there the chips only show and hide their kind.
    // 1.2.7 added an optional hint parameter, which changes the signature a direct call binds to, so ShowSet is invoked
    // by name and any parameter past the four every build has takes its default.
    private static readonly System.Reflection.MethodInfo ShowSet = HarmonyLib.AccessTools.Method(typeof(PinList), "ShowSet");
    private static readonly bool HasPinList = ShowSet != null;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ShowList(PinList list, int index, RectTransform at)
    {
        var map = Minimap.instance;
        var parameters = ShowSet.GetParameters();
        var args = new object[parameters.Length];
        args[0] = PinKinds.Names[index];
        args[1] = at;
        args[2] = (System.Func<List<Minimap.PinData>>)(() => map.m_pins.Where(pin => PinKinds.IndexOf(pin.m_type) == index).ToList());
        args[3] = (System.Action<Minimap.PinData>)(pin => map.RemovePin(pin));
        for (var i = 4; i < args.Length; i++)
            args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
        ShowSet.Invoke(list, args);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static string ListHint() => Loc.ChipListHint;

    // As SeneaL UI paints its own chips (MapView.PaintChip).
    private static void Paint(Chip chip, int index)
    {
        var on = PinKinds.Shown(index);
        chip.Bg.sprite = Skin.Sliced(28, !on ? Styles.ChipOff : chip.Hover ? Styles.ChipHover : Styles.Chip);
        var off = Palette.Hex(9207920);
        chip.Text.color = on ? Palette.Parchment : off;
        chip.Count.color = on ? Palette.Gold : off;
        chip.Icon.color = on ? Palette.Brass : off;
    }

    private static void Facing(Minimap map)
    {
        var players = Player.GetAllPlayers().Where(player => player != null && player != Player.m_localPlayer)
            .GroupBy(player => player.GetPlayerName()).ToDictionary(group => group.Key, group => group.First());
        foreach (var pin in map.m_pins)
        {
            if (pin.m_type != Minimap.PinType.Player || !MapPins.Uis.TryGetValue(pin, out var ui) || ui.Icon == null)
            {
                continue;
            }
            var yaw = players.TryGetValue(pin.m_name, out var player) ? player.transform.eulerAngles.y : 0f;
            var turn = Quaternion.Euler(0f, 0f, -yaw);
            if (ui.Icon.rectTransform.localRotation != turn)
            {
                ui.Icon.rectTransform.localRotation = turn;
            }
        }
    }

    // SeneaL UI draws a pin of a type it does not know with the pin's own picture, so each kind's pins carry that kind's SeneaL UI icon.
    private static void Icons(Minimap map)
    {
        foreach (var pin in map.m_pins)
        {
            var i = PinKinds.IndexOf(pin.m_type);
            if (i < 0)
            {
                continue;
            }
            var sprite = SeneaLUI.Theme.Icons.Get(PinKinds.Icons[i]);
            if (sprite != null && pin.m_icon != sprite)
            {
                pin.m_icon = sprite;
            }
        }
    }
}
