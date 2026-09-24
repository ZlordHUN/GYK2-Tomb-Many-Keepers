using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Bootstrap;
using LazyBearTechnology;

namespace GYK2.TombManyKeepers.UI.Mods;

internal static class ModsWindow
{
    internal static void Open(UIMainMenuWindow menu)
    {
        var mods = Chainloader.PluginInfos.Values
            .Select(plugin => plugin.Metadata)
            .OrderBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase)
            .Select(plugin => $"{plugin.Name}  {plugin.Version}").ToArray();
        var window = LazyUI.GetWindow<UIDialogWindow>();
        const int pageSize = 6;
        int pages = (mods.Length + pageSize - 1) / pageSize;
        menu.Close();
        ShowPage(0);

        void ShowPage(int page)
        {
            var buttons = new List<UIDialogWindowData.ButtonData>();
            if (page > 0)
                buttons.Add(new UIDialogWindowData.ButtonData(() => ShowPage(page - 1), "Previous",
                    replaceForGamepad: false, keyToReplace: GameKey.Select));
            if (page + 1 < pages)
                buttons.Add(new UIDialogWindowData.ButtonData(() => ShowPage(page + 1), "Next",
                    replaceForGamepad: false, keyToReplace: GameKey.Select));
            buttons.Add(new UIDialogWindowData.ButtonData(window.Close, LLBase.L("tip_back"),
                keyToReplace: GameKey.Back));
            string title = pages > 1 ? $"Mods ({page + 1}/{pages})" : "Mods";
            if (window.IsShown)
                window.CloseWithoutCallback();
            window.Open(new UIDialogWindowData(title,
                string.Join("\n", mods.Skip(page * pageSize).Take(pageSize)), buttons)
            {
                ShowCloseButton = true
            }, _ => menu.Open(null));
        }
    }
}
