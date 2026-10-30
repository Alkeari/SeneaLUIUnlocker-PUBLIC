using System;
using UnityEngine;

namespace SeneaLUIUnlocker;

// SeneaL UI's own ElementSelect dropdown, which only SeneaL UI 1.2.0 and later have; Choice uses it when the window additions are loaded.
internal sealed class SeneaLChoice : IChoice
{
    private readonly SeneaLUI.Hud.ElementSelect _select;

    public SeneaLChoice(RectTransform parent, float x, float y, float w, RectTransform popHost)
    {
        _select = new SeneaLUI.Hud.ElementSelect(parent, x, y, w, popHost);
    }

    public Action<int> OnPick
    {
        get => _select.OnPick;
        set => _select.OnPick = value;
    }

    public RectTransform Root => _select._root;

    public bool Open => _select.Open;

    public void BindNames(string[] names, int current) => _select.BindNames(names, current);

    public void SetCurrent(string name) => _select.SetCurrent(name);

    public bool Close() => _select.Close();

    public void Tick(Component owner) => _select.Tick(owner);

    public void Dispose() => _select.Dispose();
}
