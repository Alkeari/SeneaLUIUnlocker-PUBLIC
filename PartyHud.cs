using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SeneaLUIUnlocker;

// The Party HUD: one row per party member, drawn with SeneaL UI's fonts, colors and panels.
internal sealed class PartyHud
{
    public const string ElementId = "PartyHud";

    private const float RowWidth = 230f;
    private const float RowHeight = 46f;
    private const float Gap = 4f;
    private const float AvatarSize = 36f;
    private const int SortingOrder = 30;

    private readonly Party _party;
    private GameObject _leave;
    private readonly ConfigEntry<bool> _show;
    private readonly ConfigEntry<bool> _avatar;
    private readonly ConfigEntry<bool> _face;
    private readonly ConfigEntry<bool> _name;
    private readonly ConfigEntry<bool> _health;
    private readonly ConfigEntry<bool> _stamina;
    private readonly ConfigEntry<bool> _distance;
    private readonly ConfigEntry<bool> _level;
    private readonly ConfigEntry<bool> _direction;
    private readonly ConfigEntry<bool> _pvp;
    private readonly ConfigEntry<float> _x;
    private readonly ConfigEntry<float> _y;
    private readonly ConfigEntry<float> _scale;
    private readonly ConfigEntry<int> _maxRows;

    private readonly List<Row> _rows = [];
    private RectTransform _root;
    private object _element;
    private Sprite _circle;
    private Sprite _arrow;

    public PartyHud(ConfigFile cfg, Party party)
    {
        _party = party;
        const string Section = "Party HUD";
        _show = cfg.Bind(Section, "Show Party HUD", true, "Show the Party HUD while you have party members.");
        _avatar = cfg.Bind(Section, "Show Avatar", true, "Show each member's platform profile picture in a circle, or their initial when the platform gives none.");
        PartyFaces.Bind(cfg);
        _face = cfg.Bind(Section, "Face Pictures", true, "Show as each member's avatar a picture of their face without a helmet, taken in game when they are near you, in place of their platform picture.");
        _name = cfg.Bind(Section, "Show Name", true, "Show each member's name.");
        _health = cfg.Bind(Section, "Show Health", true, "Show each member's health bar.");
        _stamina = cfg.Bind(Section, "Show Stamina", true, "Show each member's stamina bar; exact for members running this mod, measured against the most seen otherwise.");
        _distance = cfg.Bind(Section, "Show Distance", true, "Show how far away each member is.");
        _level = cfg.Bind(Section, "Show Level", false, "Show each member's level, for members running this mod together with a level mod such as Skills Reworked.");
        _direction = cfg.Bind(Section, "Show Direction", false, "Show an arrow pointing toward each member, relative to where you look.");
        _pvp = cfg.Bind(Section, "Show PvP Status", false, "Mark members who have PvP on.");
        // Placed in SeneaL UI's HUD editor like its own elements; these three only place it where SeneaL API is missing, so they stay out of the mod settings page.
        var hidden = new ConfigurationManagerAttributes { Browsable = false };
        _x = cfg.Bind(Section, "Position X", 0.01f, new ConfigDescription("Left edge of the Party HUD, as a share of the screen width, where SeneaL API is not installed.", new AcceptableValueRange<float>(0f, 1f), hidden));
        _y = cfg.Bind(Section, "Position Y", 0.62f, new ConfigDescription("Top edge of the Party HUD, as a share of the screen height from the bottom, where SeneaL API is not installed.", new AcceptableValueRange<float>(0f, 1f), hidden));
        _scale = cfg.Bind(Section, "Scale", 1f, new ConfigDescription("Size of the Party HUD where SeneaL API is not installed.", new AcceptableValueRange<float>(0.5f, 2.5f), hidden));
        _maxRows = cfg.Bind(Section, "Max Rows", 8, new ConfigDescription("Most members shown at once, nearest first.", new AcceptableValueRange<int>(1, 10)));
    }

    public void Tick()
    {
        var me = Player.m_localPlayer;
        // Registered with SeneaL UI's HUD editor as soon as the HUD exists, because the editor only adds an element's frame when it rebuilds, which it does while closed.
        if (_root == null && Hud.instance != null)
        {
            Build();
        }
        var editing = SeneaLHud.Editing(ElementMode.Hud);
        var visible = _show.Value && _party.Enabled && me != null && Hud.instance != null && !Hud.IsUserHidden() && (_party.Members.Count > 0 || editing);
        if (!visible)
        {
            if (_root != null)
            {
                _root.gameObject.SetActive(false);
            }
            return;
        }
        if (_root == null)
        {
            Build();
        }
        _root.gameObject.SetActive(true);
        var actions = CursorFree && _party.Members.Count > 0;
        if (_leave.activeSelf != actions)
        {
            _leave.SetActive(actions);
        }
        if (_element == null)
        {
            _root.anchorMin = _root.anchorMax = new Vector2(_x.Value, _y.Value);
            _root.anchoredPosition = Vector2.zero;
            _root.localScale = Vector3.one * _scale.Value;
        }

        var players = Player.GetAllPlayers().ToDictionary(Party.PeerOf, p => p);
        var online = ZNet.instance.GetPlayerList().ToDictionary(info => info.m_characterID.UserID, info => info);
        var shown = _party.Members.Values
            .Select(member => (Member: member, Player: players.TryGetValue(member.Peer, out var p) ? p : null, Pos: Position(member, players, online)))
            .OrderBy(entry => entry.Pos is { } pos ? Vector3.Distance(pos, me.transform.position) : float.MaxValue)
            .Take(_maxRows.Value)
            .ToList();
        // While the HUD editor is open with nobody in the party, the player's own row stands in, so the element has something to place.
        if (shown.Count == 0 && editing)
        {
            shown.Add((new PartyMember { Peer = Party.PeerOf(me), Name = me.GetPlayerName() }, me, me.transform.position));
        }
        while (_rows.Count < shown.Count)
        {
            _rows.Add(new Row(_root, _rows.Count, _circle, _arrow, _party));
        }
        for (var i = 0; i < _rows.Count; i++)
        {
            if (i < shown.Count)
            {
                _rows[i].Show(this, shown[i].Member, shown[i].Player, shown[i].Pos, me);
            }
            else
            {
                _rows[i].Hide();
            }
        }
    }

    // The buttons on the Party HUD show while the cursor is free (the inventory or a menu is open), so they never catch a click meant for the game.
    private static bool CursorFree => InventoryGui.IsVisible() || Menu.IsVisible() || Minimap.IsOpen();

    private static Vector3? Position(PartyMember member, Dictionary<long, Player> players, Dictionary<long, ZNet.PlayerInfo> online)
    {
        if (players.TryGetValue(member.Peer, out var player) && player != null)
        {
            return player.transform.position;
        }
        return online.TryGetValue(member.Peer, out var info) && info.m_publicPosition ? info.m_position : null;
    }

    private void Build()
    {
        var canvasObject = new GameObject("SeneaLUIUnlocker_PartyHud", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        Object.DontDestroyOnLoad(canvasObject);
        var canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = SortingOrder;
        canvasObject.GetComponent<CanvasScaler>().referencePixelsPerUnit = SkinScale.Ppu;
        ResolutionScale.Track(canvasObject.GetComponent<CanvasScaler>());
        var holder = new GameObject("rows", typeof(RectTransform));
        _root = holder.GetComponent<RectTransform>();
        _root.SetParent(canvasObject.transform, false);
        _root.anchorMin = _root.anchorMax = new Vector2(_x.Value, _y.Value);
        _root.pivot = new Vector2(0f, 1f);
        _root.sizeDelta = new Vector2(RowWidth, _maxRows.Value * (RowHeight + Gap) - Gap);
        _root.anchoredPosition = Vector2.zero;
        _leave = Row.Button(_root, "leave", new Vector2(0f, 22f), "Leave party", () => _party.Leave(), "Leave the party; its members stop seeing you on their Party HUD and map.");
        _element = SeneaLHud.Register(ElementId, "Party HUD", _root);
        _circle = MakeCircle(64);
        _arrow = MakeArrow(32);
    }

    private static Sprite MakeCircle(int size)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var r = size / 2f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r));
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(r - d)));
            }
        }
        texture.Apply();
        return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
    }

    private static Sprite MakeArrow(int size)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (var y = 0; y < size; y++)
        {
            var half = (size - y) / 2f * 0.9f;
            for (var x = 0; x < size; x++)
            {
                var inside = Mathf.Abs(x + 0.5f - size / 2f) <= half && y < size - 2;
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, inside ? 1f : 0f));
            }
        }
        texture.Apply();
        return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
    }

    private sealed class Row
    {
        private readonly GameObject _object;
        private readonly Image _avatarImage;
        private readonly RawImage _avatarPicture;
        private readonly TextMeshProUGUI _initial;
        private readonly TextMeshProUGUI _name;
        private readonly TextMeshProUGUI _right;
        private readonly Bar _health;
        private readonly Bar _stamina;
        private readonly Image _arrow;
        private readonly TextMeshProUGUI _pvp;
        private readonly GameObject _actions;
        private long _peer;

        public Row(RectTransform parent, int index, Sprite circle, Sprite arrow, Party party)
        {
            _object = new GameObject("row" + index, typeof(RectTransform));
            var rect = (RectTransform)_object.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(RowWidth, RowHeight);
            rect.anchoredPosition = new Vector2(0f, -index * (RowHeight + Gap));
            SeneaLTheme.Back(rect, "Glass78", (int)RowHeight);

            var ring = Child(rect, "avatar", new Vector2(6f, -5f), new Vector2(AvatarSize, AvatarSize)).gameObject.AddComponent<Image>();
            ring.sprite = SeneaLTheme.Circle("Circ", (int)AvatarSize) ?? circle;
            ring.color = SeneaLTheme.Color("Brass", new Color(0.78f, 0.62f, 0.35f));
            var maskRect = Child(ring.rectTransform, "mask", new Vector2(2f, -2f), new Vector2(AvatarSize - 4f, AvatarSize - 4f));
            _avatarImage = maskRect.gameObject.AddComponent<Image>();
            _avatarImage.sprite = circle;
            _avatarImage.color = SeneaLTheme.Color("Ink", new Color(0.1f, 0.08f, 0.06f));
            maskRect.gameObject.AddComponent<Mask>().showMaskGraphic = true;
            var pictureRect = Child(maskRect, "picture", Vector2.zero, maskRect.sizeDelta);
            _avatarPicture = pictureRect.gameObject.AddComponent<RawImage>();
            _initial = Text(maskRect, "initial", Vector2.zero, maskRect.sizeDelta, 18f, TextAlignmentOptions.Center, "SemiBold");

            _name = Text(rect, "name", new Vector2(48f, -4f), new Vector2(120f, 18f), 15f, TextAlignmentOptions.Left, "SemiBold");
            _right = Text(rect, "right", new Vector2(150f, -4f), new Vector2(54f, 18f), 13f, TextAlignmentOptions.Right, "Regular");
            _pvp = Text(rect, "pvp", new Vector2(150f, -4f), new Vector2(54f, 18f), 12f, TextAlignmentOptions.Left, "Bold");
            _pvp.text = Lang.T("PartyHud.Pvp", "PvP");
            _pvp.color = SeneaLTheme.Color("Blood", new Color(0.8f, 0.2f, 0.15f));
            _health = new Bar(rect, "health", new Vector2(48f, -24f), new Vector2(160f, 7f), SeneaLTheme.Color("Blood", new Color(0.8f, 0.2f, 0.15f)));
            _stamina = new Bar(rect, "stamina", new Vector2(48f, -34f), new Vector2(160f, 5f), SeneaLTheme.Color("Gold", new Color(0.95f, 0.78f, 0.3f)));
            var arrowRect = Child(rect, "arrow", new Vector2(208f, -12f), new Vector2(16f, 16f));
            arrowRect.pivot = new Vector2(0.5f, 0.5f);
            arrowRect.anchoredPosition += new Vector2(8f, -8f);
            _arrow = arrowRect.gameObject.AddComponent<Image>();
            _arrow.sprite = arrow;
            _arrow.color = SeneaLTheme.Color("Parchment", new Color(0.93f, 0.88f, 0.76f));
            // Remove takes the member off this client's party list; Vote to kick asks the party to take them off everyone's.
            _actions = Child(rect, "actions", new Vector2(RowWidth + 4f, 0f), new Vector2(150f, RowHeight)).gameObject;
            Button((RectTransform)_actions.transform, "remove", new Vector2(0f, -4f), "Remove", () => party.Remove(_peer), "Take this player off your own party list.");
            Button((RectTransform)_actions.transform, "kick", new Vector2(0f, -24f), "Vote to kick", () => party.VoteToKick(_peer), "Ask the party to vote this player out; it passes when at least half the party votes yes.");
        }

        public static GameObject Button(RectTransform parent, string name, Vector2 at, string label, System.Action click, string tip)
        {
            var rect = Child(parent, name, at, new Vector2(110f, 18f));
            var back = SeneaLTheme.Back(rect, "Glass78", 18);
            back.raycastTarget = true;
            var text = Text(rect, "t", Vector2.zero, rect.sizeDelta, 12f, TextAlignmentOptions.Center, "SemiBold");
            text.text = label;
            var normal = SeneaLTheme.Color("Parchment", new Color(0.93f, 0.88f, 0.76f));
            text.color = normal;
            var input = rect.gameObject.AddComponent<UIInputHandler>();
            input.m_onLeftClick = _ => click();
            input.m_onPointerEnter = _ => text.color = SeneaLTheme.Color("Gold", new Color(0.95f, 0.78f, 0.3f));
            input.m_onPointerExit = _ => text.color = normal;
            ElementLook.Tip(rect, label, tip);
            return rect.gameObject;
        }

        public void Show(PartyHud hud, PartyMember member, Player player, Vector3? pos, Player me)
        {
            _object.SetActive(true);
            _peer = member.Peer;
            var own = member.Peer == Party.PeerOf(me);
            var actions = CursorFree && !own;
            if (_actions.activeSelf != actions)
            {
                _actions.SetActive(actions);
            }
            var near = player != null;
            var dim = near ? 1f : 0.55f;

            Texture picture = null;
            if (hud._avatar.Value && hud._face.Value)
            {
                var id = member.User.m_id.ToString();
                picture = PartyFaces.Of(string.IsNullOrEmpty(id) || own ? "self." + member.Name : id, player);
            }
            if (hud._avatar.Value && picture == null)
            {
                picture = PartyAvatars.Of(member.User);
            }
            _avatarImage.transform.parent.gameObject.SetActive(hud._avatar.Value);
            _avatarPicture.texture = picture;
            _avatarPicture.enabled = picture != null;
            _initial.text = picture == null && member.Name.Length > 0 ? member.Name.Substring(0, 1).ToUpperInvariant() : "";

            _name.gameObject.SetActive(hud._name.Value);
            var level = hud._level.Value && member.Level >= 0 ? Lang.F("PartyHud.Level", "  Lv {0}", member.Level) : "";
            var away = !own && ChatExtras.StatusOf(member.Peer) == Presence.Away ? Lang.T("PartyHud.Away", "  away") : "";
            _name.text = member.Name + level + away;
            _name.color = SeneaLTheme.Color("Parchment", new Color(0.93f, 0.88f, 0.76f)) * new Color(1f, 1f, 1f, dim);

            var distance = pos is { } at ? Vector3.Distance(at, me.transform.position) : (float?)null;
            _right.gameObject.SetActive(hud._distance.Value);
            _right.text = distance is { } d ? Lang.F("PartyHud.Distance", "{0} m", Mathf.RoundToInt(d)) : Lang.T("PartyHud.Far", "far");
            _right.color = SeneaLTheme.Color("Linen", new Color(0.8f, 0.76f, 0.66f)) * new Color(1f, 1f, 1f, dim);

            _pvp.gameObject.SetActive(hud._pvp.Value && near && player.IsPVPEnabled());
            _pvp.rectTransform.anchoredPosition = new Vector2(48f + Mathf.Min(_name.preferredWidth, 100f) + 6f, -5f);

            _health.Show(hud._health.Value, near ? player.GetHealth() / Mathf.Max(1f, player.GetMaxHealth()) : 0f, dim);
            var stamina = near ? Stamina(member, player) : 0f;
            _stamina.Show(hud._stamina.Value, stamina, dim);

            var arrowOn = hud._direction.Value && pos != null && GameCamera.instance != null;
            _arrow.gameObject.SetActive(arrowOn);
            if (arrowOn)
            {
                var to = pos.Value - me.transform.position;
                var forward = GameCamera.instance.transform.forward;
                var angle = Vector3.SignedAngle(new Vector3(forward.x, 0f, forward.z), new Vector3(to.x, 0f, to.z), Vector3.up);
                _arrow.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -angle);
            }
        }

        private static float Stamina(PartyMember member, Player player)
        {
            var zdo = player.m_nview == null ? null : player.m_nview.GetZDO();
            var now = zdo == null ? 0f : zdo.GetFloat(ZDOVars.s_stamina, member.SeenStamina);
            member.SeenStamina = Mathf.Max(member.SeenStamina, now);
            var max = member.HasMod && member.MaxStamina > 0f ? member.MaxStamina : member.SeenStamina;
            return now / Mathf.Max(1f, max);
        }

        public void Hide() => _object.SetActive(false);

        private static RectTransform Child(RectTransform parent, string name, Vector2 at, Vector2 size)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = at;
            rect.sizeDelta = size;
            return rect;
        }

        private static TextMeshProUGUI Text(RectTransform parent, string name, Vector2 at, Vector2 size, float fontSize, TextAlignmentOptions align, string font)
        {
            // Inactive until the font is set, so TextMeshPro never wakes looking for the default font the game does not ship.
            var host = Child(parent, name, at, size).gameObject;
            host.SetActive(false);
            var text = host.AddComponent<TextMeshProUGUI>();
            var asset = SeneaLTheme.Font(font);
            if (asset != null)
            {
                text.font = asset;
            }
            host.SetActive(true);
            text.fontSize = fontSize;
            text.alignment = align;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Ellipsis;
            // Ellipsis hides a line that is taller than its box, so the box is never shorter than one line of the text.
            var box = text.rectTransform;
            box.sizeDelta = new Vector2(box.sizeDelta.x, Mathf.Max(box.sizeDelta.y, Mathf.Ceil(fontSize * 1.45f)));
            text.raycastTarget = false;
            return text;
        }

        private sealed class Bar
        {
            private readonly RectTransform _track;
            private readonly RectTransform _fill;
            private readonly Image _fillImage;
            private readonly Color _color;
            private readonly float _width;

            public Bar(RectTransform parent, string name, Vector2 at, Vector2 size, Color color)
            {
                _color = color;
                _width = size.x;
                _track = Child(parent, name, at, size);
                var back = _track.gameObject.AddComponent<Image>();
                var (sprite, _) = SeneaLTheme.Panel("Track", (int)size.y);
                back.sprite = sprite;
                back.type = sprite == null ? Image.Type.Simple : Image.Type.Sliced;
                back.color = sprite == null ? new Color(0f, 0f, 0f, 0.5f) : Color.white;
                _fill = Child(_track, "fill", Vector2.zero, size);
                _fillImage = _fill.gameObject.AddComponent<Image>();
            }

            public void Show(bool on, float share, float dim)
            {
                _track.gameObject.SetActive(on);
                _fill.sizeDelta = new Vector2(_width * Mathf.Clamp01(share), _fill.sizeDelta.y);
                _fillImage.color = _color * new Color(1f, 1f, 1f, dim);
            }
        }
    }
}
